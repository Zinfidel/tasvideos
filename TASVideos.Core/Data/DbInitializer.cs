﻿using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TASVideos.Core.Services;
using TASVideos.Core.Settings;
using TASVideos.Data;
using TASVideos.Data.Entity;
using TASVideos.Data.SeedData;
using TASVideos.WikiEngine;
using SharpCompress.Compressors;

namespace TASVideos.Core.Data
{
	public static class DbInitializer
	{
		public static void InitializeDatabase(IServiceProvider services)
		{
			var settings = services.GetRequiredService<IOptions<AppSettings>>();
			switch (settings.Value.GetStartupStrategy())
			{
				case StartupStrategy.Minimal:
					MinimalStrategy(services);
					break;
				case StartupStrategy.Sample:
					SampleStrategy(services);
					break;
				case StartupStrategy.Migrate:
					MigrationStrategy(services);
					break;
			}
		}

		private static void MinimalStrategy(IServiceProvider services)
		{
			var context = services.GetRequiredService<ApplicationDbContext>();
			context.Database.EnsureCreated();
			var wikiPages = services.GetRequiredService<IWikiPages>();
			wikiPages.PrePopulateCache();
		}

		private static void SampleStrategy(IServiceProvider services)
		{
			var context = services.GetRequiredService<ApplicationDbContext>();
			Initialize(context);

			// Note: We specifically do not want to run seed data
			// This data is already baked into the sample data file
			GenerateDevSampleData(context).Wait();
		}

		private static void MigrationStrategy(IServiceProvider services)
		{
			var context = services.GetRequiredService<ApplicationDbContext>();
			context.Database.Migrate();
		}

		/// <summary>
		/// Creates the database and seeds it with necessary seed data
		/// Seed data is necessary data for a production release.
		/// </summary>
		public static void Initialize(DbContext context)
		{
			// For now, always delete then recreate the database
			// When the database is more mature we will move towards the Migrations process
			context.Database.EnsureDeleted();
			context.Database.EnsureCreated();
		}

		/// <summary>
		/// Adds data necessary for production, should be run before legacy migration processes.
		/// </summary>
		public static void PreMigrateSeedData(ApplicationDbContext context)
		{
			context.Roles.AddRange(RoleSeedData.AllRoles);
			context.GameSystems.AddRange(SystemSeedData.Systems);
			context.GameSystemFrameRates.AddRange(SystemSeedData.SystemFrameRates);
			context.PublicationClasses.AddRange(PublicationClassSeedData.Classes);
			context.Genres.AddRange(GenreSeedData.Genres);
			context.Flags.AddRange(FlagSeedData.Flags);
			context.SubmissionRejectionReasons.AddRange(RejectionReasonsSeedData.RejectionReasons);
			context.IpBans.AddRange(IpBanSeedData.IpBans);
			context.SaveChanges();
		}

		public static void PostMigrateSeedData(ApplicationDbContext context)
		{
			foreach (var wikiPage in WikiPageSeedData.NewRevisions)
			{
				var currentRevision = context.WikiPages
					.Where(wp => wp.PageName == wikiPage.PageName)
					.SingleOrDefault(wp => wp.Child == null);

				if (currentRevision is not null)
				{
					wikiPage.Revision = currentRevision.Revision + 1;
					currentRevision.Child = wikiPage;
				}

				context.WikiPages.Add(wikiPage);
				var referrals = Util.GetReferrals(wikiPage.Markup);
				foreach (var referral in referrals)
				{
					context.WikiReferrals.Add(new WikiPageReferral
					{
						Referrer = wikiPage.PageName,
						Referral = referral.Link,
						Excerpt = referral.Excerpt
					});
				}
			}

			context.SaveChanges();
		}

		/// <summary>
		/// Adds optional sample users for each role in the system for testing purposes
		/// Roles must already exist before running this
		/// DO NOT run this on production environments! This generates users with high level access and a default and public password.
		/// </summary>
		public static async Task GenerateDevTestUsers(ApplicationDbContext context, UserManager userManager, AppSettings settings)
		{
			// Add users for each Role for testing purposes
			var roles = await context.Roles.ToListAsync();
			var defaultRoles = roles.Where(r => r.IsDefault).ToList();

			foreach (var role in roles.Where(r => !r.IsDefault))
			{
				var user = new User
				{
					UserName = role.Name.Replace(" ", ""),
					NormalizedUserName = role.Name.Replace(" ", "").ToUpper(),
					Email = role.Name + "@example.com",
					TimeZoneId = "Eastern Standard Time"
				};
				var result = await userManager.CreateAsync(user, settings.SampleDataPassword);
				if (!result.Succeeded)
				{
					throw new Exception(string.Join(",", result.Errors.Select(e => e.ToString())));
				}

				var savedUser = context.Users.Single(u => u.UserName == user.UserName);
				savedUser.EmailConfirmed = true;
				savedUser.LockoutEnabled = false;
				context.UserRoles.Add(new UserRole { Role = role, User = savedUser });
				foreach (var defaultRole in defaultRoles)
				{
					context.UserRoles.Add(new UserRole { Role = defaultRole, User = savedUser });
				}
			}

			await context.SaveChangesAsync();
		}

		/// <summary>
		/// Adds optional sample data
		/// Unlike seed data, sample data is arbitrary data for testing purposes and would not be apart of a production release.
		/// </summary>
		private static async Task GenerateDevSampleData(DbContext context)
		{
			var sql = await GetSampleDataScript();
			await using (await context.Database.BeginTransactionAsync())
			{
				var commands = new[] { sql };

				foreach (var c in commands)
				{
					// EF needs this BS for some reason
					var escaped = c
						.Replace("{", "{{")
						.Replace("}", "}}");

					await context.Database.ExecuteSqlRawAsync(escaped);
				}

				await context.Database.CommitTransactionAsync();
			}
		}

		private static async Task<string> GetSampleDataScript()
		{
			var bytes = await GetSampleDataFile();
			await using var ms = new MemoryStream(bytes);
			await using var gz = new SharpCompress.Compressors.Deflate.GZipStream(ms, CompressionMode.Decompress);
			using var unzip = new StreamReader(gz);
			return await unzip.ReadToEndAsync();
		}

		private static async Task<byte[]> GetSampleDataFile()
		{
			byte[] bytes;
			string sampleDataPath = Path.Combine(Path.GetTempPath(), "sample-data.sql.gz");
			if (!File.Exists(sampleDataPath))
			{
				bytes = await DownloadSampleDataFile();
				await File.WriteAllBytesAsync(sampleDataPath, bytes);
			}
			else
			{
				var createDate = File.GetLastWriteTimeUtc(sampleDataPath);
				if (createDate < DateTime.UtcNow.AddDays(-1))
				{
					bytes = await DownloadSampleDataFile();
					await File.WriteAllBytesAsync(sampleDataPath, bytes);
				}
				else
				{
					bytes = await File.ReadAllBytesAsync(sampleDataPath);
				}
			}

			return bytes;
		}

		private static async Task<byte[]> DownloadSampleDataFile()
		{
			// TODO: remove staging after go-live
			const string url = "https://tasvideos.org/sample-data/sample-data.sql.gz";
			using var client = new HttpClient();
			using var result = await client.GetAsync(url);
			result.EnsureSuccessStatusCode();
			return await result.Content.ReadAsByteArrayAsync();
		}
	}
}
