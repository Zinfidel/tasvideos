﻿using System;
using System.Linq;
using System.Web;
using Microsoft.EntityFrameworkCore;
using TASVideos.Data;
using TASVideos.Data.Entity.Forum;
using TASVideos.Legacy.Data.Forum;

namespace TASVideos.Legacy.Imports
{
	internal static class ForumPollImporter
	{
		public static void Import(ApplicationDbContext context, NesVideosForumContext legacyForumContext)
		{
			const int awardForumId = 21;
			var legacyVoteDescriptions = legacyForumContext.VoteDescription
				.Include(v => v.Topic)
				.ThenInclude(t => t!.Poster)
				.Include(v => v.Results)
				.Where(v => v.Topic != null && v.Topic.TopicMovedId == 0)
				.ToList();

			/******** ForumPoll ********/
			var forumPolls = legacyVoteDescriptions
				.Select(v => new ForumPoll
				{
					Id = v.Id,
					TopicId = v.TopicId,
					Question = SwapSubmissionPoll(ImportHelper.ConvertNotNullLatin1String(v.Text), v.Topic?.Poster?.UserName),
					CreateTimestamp = ImportHelper.UnixTimeStampToDateTime(v.VoteStart),
					CreateUserName = v.Topic?.Poster?.UserName ?? "Unknown",
					LastUpdateUserName = v.Topic?.Poster?.UserName ?? "Unknown",
					CloseDate = v.VoteLength == 0
						? null
						: ImportHelper.UnixTimeStampToDateTime(v.VoteStart + v.VoteLength),
					MultiSelect = v.Topic!.ForumId == awardForumId
				})
				.ToList();

			var pollColumns = new[]
			{
				nameof(ForumPoll.Id),
				nameof(ForumPoll.TopicId),
				nameof(ForumPoll.Question),
				nameof(ForumPoll.CloseDate),
				nameof(ForumPoll.MultiSelect),
				nameof(ForumPoll.CreateTimestamp),
				nameof(ForumPoll.CreateUserName),
				nameof(ForumPoll.LastUpdateTimestamp),
				nameof(ForumPoll.LastUpdateUserName)
			};

			forumPolls.BulkInsert(pollColumns, nameof(ApplicationDbContext.ForumPolls));

			/******** ForumPollOption ********/
			var legForumPollOptions = legacyVoteDescriptions
				.SelectMany(vd => vd.Results)
				.ToList();

			var forumPollOptions = legForumPollOptions
			.Select(r => new
				{
					Text = r.VoteOptionText,
					PollId = r.Id,
					Ordinal = r.VoteOptionId,
					Voter = r.VoteDescription?.Topic?.Poster?.UserName ?? "Unknown"
				})
				.ToList()
			.Select(po => new ForumPollOption
			{
				Text = HttpUtility.HtmlDecode(ImportHelper.ConvertNotNullLatin1String(po.Text)),
				PollId = po.PollId,
				Ordinal = po.Ordinal,
				CreateTimestamp = DateTime.UtcNow,
				LastUpdateTimestamp = DateTime.UtcNow,
				CreateUserName = po.Voter,
				LastUpdateUserName = po.Voter
			});

			var pollOptionColumns = new[]
			{
				nameof(ForumPollOption.Text),
				nameof(ForumPollOption.Ordinal),
				nameof(ForumPollOption.PollId),
				nameof(ForumPollOption.CreateTimestamp),
				nameof(ForumPollOption.CreateUserName),
				nameof(ForumPollOption.LastUpdateTimestamp),
				nameof(ForumPollOption.LastUpdateUserName)
			};

			forumPollOptions.BulkInsert(pollOptionColumns, nameof(ApplicationDbContext.ForumPollOptions));

			/******** ForumPollOptionVote ********/
			var legForumVoters = legacyForumContext.Voter.ToList();
			var newForumOptions = context.ForumPollOptions.ToList();

			var forumPollOptionVotes =
				(from v in legForumVoters
				join po in newForumOptions on new { PollId = v.Id, Ordinal = v.OptionId } equals new { po.PollId, po.Ordinal }
				where v.UserId != 8762 // This user was deleted but had votes, just remove them and fill them in below
				select new ForumPollOptionVote
				{
					PollOptionId = po.Id,
					UserId = v.UserId,
					IpAddress = v.IpAddress.IpFromHex(),
					CreateTimestamp = DateTime.UtcNow // Legacy system did not track this
				})
				.ToList();

			// Insert Unknown User votes for discrepancies between de-normalized vote count and actual vote records
			var missingVotes =
				(from po in legForumPollOptions
				 join v in legForumVoters on new { po.Id, OptionId = po.VoteOptionId } equals new { v.Id, v.OptionId }
				 join newPo in newForumOptions on new { PollId = v.Id, Ordinal = po.VoteOptionId } equals new { newPo.PollId, newPo.Ordinal }
				 select new { po, v, newPo })
				.GroupBy(tkey => new { tkey.newPo.Id, tkey.po.ResultCount })
				.Select(g => new { g.Key.Id, g.Key.ResultCount, Actual = g.Count() })
				.Where(x => x.ResultCount > x.Actual)
				.ToList();

			foreach (var option in missingVotes)
			{
				var diff = option.ResultCount - option.Actual;

				for (int i = 0; i < diff; i++)
				{
					forumPollOptionVotes.Add(new ForumPollOptionVote
					{
						PollOptionId = option.Id,
						UserId = -1,
						IpAddress = null,
						CreateTimestamp = DateTime.UtcNow
					});
				}
			}

			var pollVoteColumns = new[]
			{
				nameof(ForumPollOptionVote.PollOptionId),
				nameof(ForumPollOptionVote.UserId),
				nameof(ForumPollOptionVote.CreateTimestamp),
				nameof(ForumPollOptionVote.IpAddress)
			};

			forumPollOptionVotes.BulkInsert(pollVoteColumns, nameof(ApplicationDbContext.ForumPollOptionVotes));
		}

		// Removes html silliness from the poll questions
		private static string SwapSubmissionPoll(string pollText, string? user)
		{
			// the latter user was an April Fool's gag
			if ((user == SiteGlobalConstants.TASVideoAgent || user == "poozilla") && pollText.StartsWith("Vote: "))
			{
				if (pollText.StartsWith("Vote: Did you like watching this movie?"))
				{
					return "Vote: Did you like watching this movie? (Vote after watching!)";
				}

				if (pollText.StartsWith("Vote: Should this movie be published?"))
				{
					return "Vote: Should this movie be published? (Vote after watching!)";
				}

				return "Vote: Did you find this movie entertaining? (Vote after watching!)";
			}

			return pollText;
		}
	}
}
