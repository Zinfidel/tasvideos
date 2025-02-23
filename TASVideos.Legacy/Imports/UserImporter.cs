﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Microsoft.EntityFrameworkCore;
using TASVideos.Data;
using TASVideos.Data.Entity;
using TASVideos.Data.SeedData;
using TASVideos.Extensions;
using TASVideos.Legacy.Data.Forum;
using TASVideos.Legacy.Data.Site;

// ReSharper disable CommentTypo
// ReSharper disable IdentifierTypo
namespace TASVideos.Legacy.Imports
{
	internal static class UserImporter
	{
		private const int ModeratorGroupId = 272; // This isn't going to change, so just hard code it
		private const int EmulatorCoder = 40; // The rank id in the ranks table

		private static readonly int[] UserRatingBanList = { 7194, 4805, 4485, 5243, 635, 3301 }; // These users where explicitly banned from rating

		// Dup accounts we do not want to migrate over
		private static readonly int[] BlackList = { 4079, 4854, 6177 };

		public static IReadOnlyDictionary<int, int> Import(
			ApplicationDbContext context,
			NesVideosSiteContext legacySiteContext,
			NesVideosForumContext legacyForumContext)
		{
			// TODO: what to do about these??
			// var wikiNoForum = legacyUsers
			// .Select(u => u.Name)
			// .Except(legacyForumUsers.Select(u => u.UserName))
			// .ToList();
			var legacyUsers = legacySiteContext.Users
				.Include(u => u.UserRoles)
				.ThenInclude(ur => ur.Role)
				.ToList();

			var emuCoders = legacyForumContext.UserRanks
				.Where(ur => ur.RankId == EmulatorCoder)
				.ToList();

			var legacyForumUsers = legacyForumContext.Users
				.Where(u => u.UserName != "Anonymous")
				.Where(u => !BlackList.Contains(u.UserId))
				.Where(u => u.Email != "")
				.ToList();

			var banList = legacyForumContext.BanList
				.Where(b => b.UserId > 0)
				.ToList();

			// These were dug up from user_exceptions, which only has a few entries and complicated to parse, simpler to have a hardcoded list
			var userExceptions = new[] { 150, 590, 905, 1210, 2659, 2758, 3254 };

			var users = (from u in legacyForumUsers
						join b in banList on u.UserId equals b.UserId into bb
						from b in bb.DefaultIfEmpty()
						join be in banList on u.Email equals be.Email into bbe
						from be in bbe.DefaultIfEmpty()
						join bex in userExceptions on u.UserId equals bex into bbex
						from bex in bbe.DefaultIfEmpty()
						join e in emuCoders on u.UserId equals e.UserId into ee
						from e in ee.DefaultIfEmpty()
						join ug in legacyForumContext.UserGroups on new { u.UserId, GroupId = ModeratorGroupId } equals new { ug.UserId, ug.GroupId } into ugg
						from ug in ugg.DefaultIfEmpty()
						select new
						{
							Id = u.UserId,
							u.IsActive,
							u.UserName,
							u.RegDate,
							u.Password,
							u.EmailTime,
							u.PostCount,
							u.Email,
							u.Avatar,
							u.From,
							u.Signature,
							u.PublicRatings,
							u.LastVisitDate,
							u.TimeZoneOffset,
							u.BbcodeUid,
							IsBanned = b != null || be != null || bex != null,
							IsModerator = ug != null,
							IsForumAdmin = u.UserLevel == 1,
							IsEmuCoder = e != null,
							u.MoodAvatar,
							u.Gender
						})
						.ToList();

			var timeZones = TimeZoneInfo.GetSystemTimeZones();
			var utc = TimeZoneInfo.Utc.StandardName;
			var userEntities = users
				.Select(u => new User
				{
					Id = u.Id,
					UserName = ImportHelper.ConvertNotNullLatin1String(u.UserName).Trim(),
					NormalizedUserName = ImportHelper.ConvertNotNullLatin1String(u.UserName).ToUpper(),
					CreateTimestamp = ImportHelper.UnixTimeStampToDateTime(u.RegDate),
					LastUpdateTimestamp = ImportHelper.UnixTimeStampToDateTime(u.RegDate),
					LegacyPassword = u.Password,
					EmailConfirmed = u.IsActive || u.EmailTime != null || u.PostCount > 0,
					Email = u.Email,
					NormalizedEmail = u.Email?.ToUpper(),
					CreateUserName = "Automatic Migration",
					PasswordHash = "",
					Avatar = u.Avatar,
					From = WebUtility.HtmlDecode(ImportHelper.ConvertLatin1String(u.From)),
					Signature = WebUtility
						.HtmlDecode(
							ImportHelper.ConvertLatin1String(u.Signature?.Replace(":" + u.BbcodeUid, "")))
						.Cap(1000),
					PublicRatings = u.PublicRatings,
					LastLoggedInTimeStamp = ImportHelper.UnixTimeStampToDateTime(u.LastVisitDate),

					// ReSharper disable once CompareOfFloatsByEqualityOperator
					TimeZoneId = timeZones.FirstOrDefault(t => t.BaseUtcOffset.TotalMinutes / 60 == (double)u.TimeZoneOffset)?.StandardName ?? utc,
					SecurityStamp = Guid.NewGuid().ToString("D"),
					UseRatings = !u.IsBanned && !UserRatingBanList.Contains(u.Id),
					MoodAvatarUrlBase = u.MoodAvatar.NullIfWhiteSpace(),
					PreferredPronouns = MapPronoun(u.Gender)
				})
				.ToList();

			// Hacks
			var grue = userEntities.First(u => u.UserName == "TASVideos Grue");
			grue.UserName = "TASVideosGrue";

			var roles = context.Roles.ToList();

			var userRoles = new List<UserRole>();

			// User's site name is chinese characters, but forum name is not
			var xipo = legacyUsers.Single(u => u.Id == 1057);
			xipo.Name = "xipo";

			var joinedUsers = (from user in users
					join su in legacyUsers on user.UserName.ToLower() equals su.Name.ToLower() into lsu
					from su in lsu.DefaultIfEmpty()
					select new { User = user, SiteUser = su })
				.ToList();

			foreach (var user in joinedUsers)
			{
				if (user.SiteUser is not null)
				{
					// not having user means they are effectively banned
					// limited = Limited User
					if ((user.SiteUser.UserRoles.Any(ur => ur.Role!.Name == "user") || user.User.Id == 505) // 505 is TASVideoAgent, which needs some roles but is not a user
						&& user.SiteUser.UserRoles.All(ur => ur.Role!.Name != "admin")) // There's no point in adding these roles to admins, they have these perms anyway
					{
						if (!user.User.IsBanned)
						{
							if (user.SiteUser.UserRoles.Any(ur => ur.Role!.Name == "limited"))
							{
								userRoles.Add(new UserRole
								{
									RoleId = roles.Single(r => r.Name == RoleSeedNames.LimitedUser).Id,
									UserId = user.User.Id
								});
							}
							else
							{
								userRoles.Add(new UserRole
								{
									RoleId = roles.Single(r => r.Name == RoleSeedNames.DefaultUser).Id,
									UserId = user.User.Id
								});
							}

							if (user.User.PostCount >= SiteGlobalConstants.VestedPostCount)
							{
								userRoles.Add(new UserRole
								{
									RoleId = roles.Single(r => r.Name == RoleSeedNames.ExperiencedForumUser).Id,
									UserId = user.User.Id
								});
							}
						}
					}

					if (user.User.IsForumAdmin
						&& user.SiteUser.UserRoles.All(ur => ur.Role!.Name != "admin")) // There's no point in adding roles to admins, they have these perms anyway
					{
						userRoles.Add(new UserRole
						{
							RoleId = roles.Single(r => r.Name == RoleSeedNames.ForumAdmin).Id,
							UserId = user.User.Id
						});
					}
					else if (user.User.IsModerator
						&& user.SiteUser.UserRoles.All(ur => ur.Role!.Name != "admin")) // There's no point in adding roles to admins, they have these perms anyway
					{
						userRoles.Add(new UserRole
						{
							RoleId = roles.Single(r => r.Name == RoleSeedNames.ForumModerator).Id,
							UserId = user.User.Id
						});
					}

					if (user.User.IsEmuCoder)
					{
						userRoles.Add(new UserRole
						{
							RoleId = roles.Single(r => r.Name == RoleSeedNames.EmulatorCoder).Id,
							UserId = user.User.Id
						});
					}

					foreach (var userRole in user.SiteUser.UserRoles.Select(ur => ur.Role)
						.Where(r => r!.Name != "user" && r.Name != "limited"))
					{
						var role = GetRoleFromLegacy(userRole!.Name, roles);
						if (role is not null)
						{
							userRoles.Add(new UserRole
							{
								RoleId = role.Id,
								UserId = user.User.Id
							});
						}
					}
				}
			}

			userEntities.Add(new User
			{
				Id = -1,
				UserName = "Unknown User",
				NormalizedUserName = "UNKNOWN USER",
				Email = "unknown@example.com",
				EmailConfirmed = true,
				LegacyPassword = ""
			});

			// Some published authors that have no forum account
			// Note that by having no password nor legacy password they effectively can not log in without a database change
			// I think this is correct since these are not active users
			var portedPlayerNames = new[]
			{
				"Morimoto",
				"Tokushin",
				"Yy",
				"Mathieu P",
				"Linnom",
				"Mclaud2000",
				"Ryosuke",
				"JuanPablo",
				"qcommand",
				"Mana.",
				"RaverMeister",
				"snark",
				"ToT",

				// Submitters with no published movies
				"Deathray",
				"Ginger",
				"JakeRyansDad",
				"KMFDManic",
				"Remy B.",
				"ScouSin",
				"Vazor",
				"KennyBoy",
				"megaman",
				"Oguz",
				"Vlass14",
				"VladimirContreras",
				"Anonymous6327",
				"dex88"
			};

			var portedPlayers = portedPlayerNames.Select(p => new User
			{
				UserName = p,
				NormalizedUserName = p.ToUpper(),
				Email = $"imported{p}@tasvideos.org",
				NormalizedEmail = $"imported{p}@tasvideos.org".ToUpper(),
				CreateTimestamp = DateTime.UtcNow,
				LastUpdateTimestamp = DateTime.UtcNow,
				SecurityStamp = Guid.NewGuid().ToString("D")
			});

			var userColumns = new[]
			{
				nameof(User.Id),
				nameof(User.UserName),
				nameof(User.NormalizedUserName),
				nameof(User.CreateTimestamp),
				nameof(User.LegacyPassword),
				nameof(User.EmailConfirmed),
				nameof(User.Email),
				nameof(User.NormalizedEmail),
				nameof(User.CreateUserName),
				nameof(User.PasswordHash),
				nameof(User.AccessFailedCount),
				nameof(User.LastUpdateTimestamp),
				nameof(User.LockoutEnabled),
				nameof(User.PhoneNumberConfirmed),
				nameof(User.TwoFactorEnabled),
				nameof(User.Avatar),
				nameof(User.From),
				nameof(User.Signature),
				nameof(User.PublicRatings),
				nameof(User.LastLoggedInTimeStamp),
				nameof(User.TimeZoneId),
				nameof(User.SecurityStamp),
				nameof(User.UseRatings),
				nameof(User.MoodAvatarUrlBase),
				nameof(User.PreferredPronouns)
			};

			var userRoleColumns = new[]
			{
				nameof(UserRole.UserId),
				nameof(UserRole.RoleId)
			};

			userEntities.BulkInsert(userColumns, nameof(ApplicationDbContext.Users));
			userRoles.BulkInsert(userRoleColumns, nameof(ApplicationDbContext.UserRoles));

			var playerColumns = userColumns.Where(p => p != nameof(User.Id)).ToArray();
			portedPlayers.BulkInsert(playerColumns, nameof(ApplicationDbContext.Users));

			var mapping = joinedUsers
				.Where(u => u.SiteUser != null)
				.ToDictionary(tkey => tkey.SiteUser!.Id, tvalue => tvalue.User.Id);

			// Add specific users added above that have submissions, so we need them in the mapping
			var user43 = context.Users.Single(u => u.UserName == "ScouSin").Id;
			mapping[43] = user43;
			var user394 = context.Users.Single(u => u.UserName == "KMFDManic").Id;
			mapping[394] = user394;
			var user577 = context.Users.Single(u => u.UserName == "KennyBoy").Id;
			mapping[577] = user577;
			var user634 = context.Users.Single(u => u.UserName == "Deathray").Id;
			mapping[634] = user634;
			var user636 = context.Users.Single(u => u.UserName == "Ginger").Id;
			mapping[636] = user636;
			var user664 = context.Users.Single(u => u.UserName == "megaman").Id;
			mapping[664] = user664;
			var user1806 = context.Users.Single(u => u.UserName == "Vlass14").Id;
			mapping[1806] = user1806;
			var user1855 = context.Users.Single(u => u.UserName == "dex88").Id;
			mapping[1855] = user1855;

			mapping[1867] = user1806; // Vlass alt account

			var user2889 = context.Users.Single(u => u.UserName == "Anonymous6327").Id;
			mapping[2889] = user2889;

			mapping[391] = 1835; // super ninja, already has another wiki account
			mapping[5094] = 6227; // FitterSpace

			mapping[3270] = 7170; // Tounet01 became Doc Skellington
			mapping[6584] = 8671; // Fencypo became Snodeca
			mapping[1419] = 3737; // mega_man_3 became GlitchMan
			mapping[1971] = 4501; // DJSecret became DarkMoon

			return mapping;
		}

		private static Role? GetRoleFromLegacy(string role, IEnumerable<Role> roles)
		{
			return role.ToLower() switch
			{
				"editor" => roles.Single(r => r.Name == RoleSeedNames.Editor),
				"vestededitor" => roles.Single(r => r.Name == RoleSeedNames.VestedEditor),
				"publisher" => roles.Single(r => r.Name == RoleSeedNames.Publisher),
				"seniorpublisher" => roles.Single(r => r.Name == RoleSeedNames.SeniorPublisher),
				"judge" => roles.Single(r => r.Name == RoleSeedNames.Judge),
				"seniorjudge" => roles.Single(r => r.Name == RoleSeedNames.SeniorJudge),
				"adminassistant" => roles.Single(r => r.Name == RoleSeedNames.AdminAssistant),
				"ambassador" => roles.Single(r => r.Name == RoleSeedNames.Ambassador),
				"senior ambassador" => roles.Single(r => r.Name == RoleSeedNames.SeniorAmbassador),
				"admin" => roles.Single(r => r.Name == RoleSeedNames.Admin),
				"sitedeveloper" => roles.Single(r => r.Name == RoleSeedNames.SiteDeveloper),
				_ => null
			};
		}

		private static PreferredPronounTypes MapPronoun(string? gender)
		{
			if (string.IsNullOrWhiteSpace(gender))
			{
				return PreferredPronounTypes.Unspecified;
			}

			var result = gender.ToLower() switch
			{
				"â™‚" => PreferredPronounTypes.HeHim, // Male
				"â™€" => PreferredPronounTypes.SheHer, // Female
				"âš¥" => PreferredPronounTypes.Any, // Male & Female
				"âšª" => PreferredPronounTypes.TheyThem, // Genderless
				"?" => PreferredPronounTypes.Other, // Other
				_ => PreferredPronounTypes.Unspecified
			};

			return result;
		}
	}
}
