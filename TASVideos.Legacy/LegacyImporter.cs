﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TASVideos.Core.Services;
using TASVideos.Data;
using TASVideos.Legacy.Data.Forum;
using TASVideos.Legacy.Data.Forum.Entity;
using TASVideos.Legacy.Data.Site;
using TASVideos.Legacy.Imports;

namespace TASVideos.Legacy
{
	public interface ILegacyImporter
	{
		void RunLegacyImport();
	}

	public class LegacyImporter : ILegacyImporter
	{
		private readonly IHostEnvironment _env;
		private readonly ApplicationDbContext _db;
		private readonly NesVideosSiteContext _legacySiteDb;
		private readonly NesVideosForumContext _legacyForumDb;
		private readonly ICacheService _cache;
		private readonly ILogger<LegacyImporter> _logger;

		private static readonly Dictionary<string, long> ImportDurations = new ();

		public LegacyImporter(
			IHostEnvironment env,
			ApplicationDbContext db,
			NesVideosSiteContext legacySiteDb,
			NesVideosForumContext legacyForumDb,
			ICacheService cache,
			ILogger<LegacyImporter> logger)
		{
			_env = env;
			_db = db;
			_legacySiteDb = legacySiteDb;
			_legacyForumDb = legacyForumDb;
			_cache = cache;
			_logger = logger;
		}

		public void RunLegacyImport()
		{
			_logger.LogInformation("Beginning Import");

			string connectionStr = _db.Database.GetConnectionString();
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

			// Since we are using this database in a read-only way, set no tracking globally
			// To speed up query executions
			_legacySiteDb.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
			_legacyForumDb.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

			var stopwatch = Stopwatch.StartNew();
			SqlBulkImporter.BeginImport(connectionStr);

			if (!_cache.TryGetValue(ImportSteps.SmallTables, out bool _))
			{
				Run("Tags", () => TagImporter.Import(_legacySiteDb));
				Run("Roms", () => RomImporter.Import(_legacySiteDb));
				Run("Games", () => GameImporter.Import(_legacySiteDb));
				Run("GameGroup", () => GameGroupImporter.Import(_legacySiteDb));
				Run("GameGenre", () => GameGenreImport.Import(_legacySiteDb));
				Run("RamAddresses", () => RamAddressImporter.Import(_db, _legacySiteDb));
				_cache.Set(ImportSteps.SmallTables, true, Durations.OneDayInSeconds);
			}
			else
			{
				_logger.LogInformation($"Skipping import step: {ImportSteps.SmallTables}");
			}

			if (_cache.TryGetValue(ImportSteps.Users, out IReadOnlyDictionary<int, int> userIdMapping))
			{
				_logger.LogInformation($"Skipping import step: {ImportSteps.Users}");
			}
			else
			{
				userIdMapping = RunUserImport();
				Run("UserMaintenanceLogs", () => UserMaintenanceLogImporter.Import(_legacySiteDb, userIdMapping));
				Run("UserDisallows", () => DisallowImporter.Import(_legacyForumDb));
				Run("Awards", () => AwardImporter.Import(_legacySiteDb, userIdMapping));
				_cache.Set(ImportSteps.Users, userIdMapping, Durations.OneDayInSeconds);
			}

			if (!_cache.TryGetValue(ImportSteps.Forum, out bool _))
			{
				Run("Forum Categories", () => ForumCategoriesImporter.Import(_legacyForumDb));
				Run("Forums", () => ForumImporter.Import(_legacyForumDb));
				Run("Forum Topics", () => ForumTopicImporter.Import(_legacyForumDb));
				Run("Forum Private Messages", () => ForumPrivateMessagesImporter.Import(_legacyForumDb));
				Run("Forum Polls", () => ForumPollImporter.Import(_db, _legacyForumDb));

				// We don't want to copy these to other environments, as they can cause users to get unwanted emails
				if (_env.IsProduction())
				{
					Run("Forum Topic Watch", () => ForumTopicWatchImporter.Import(_legacyForumDb));
				}

				_cache.Set(ImportSteps.Forum, true, Durations.OneDayInSeconds);
			}
			else
			{
				_logger.LogInformation($"Skipping import step: {ImportSteps.Forum}");
			}

			if (!_cache.TryGetValue(ImportSteps.ForumPosts, out bool _))
			{
				Run("Forum Posts", () => ForumPostsImporter.Import(_legacyForumDb));
				_cache.Set(ImportSteps.ForumPosts, true, Durations.OneDayInSeconds);
			}
			else
			{
				_logger.LogInformation($"Skipping import step: {ImportSteps.ForumPosts}");
			}

			if (!_cache.TryGetValue(ImportSteps.Wiki, out bool _))
			{
				Run("Wiki", () => WikiImporter.Import(_legacySiteDb, userIdMapping));
				Run("WikiCleanup", () => WikiPageCleanup.Fix(_db, _legacySiteDb));
				Run("WikiReferral", () => WikiReferralGenerator.Generate(_db));
				_cache.Set(ImportSteps.Wiki, true, Durations.OneDayInSeconds);
			}
			else
			{
				_logger.LogInformation($"Skipping import step: {ImportSteps.Wiki}");
			}

			if (!_cache.TryGetValue(ImportSteps.Submissions, out bool _))
			{
				Run("Submissions", () => SubmissionImporter.Import(_db, _legacySiteDb, userIdMapping));
				Run("Submissions Framerate", () => SubmissionFrameRateImporter.Import(_db));
				_cache.Set(ImportSteps.Submissions, true, Durations.OneDayInSeconds);
			}
			else
			{
				_logger.LogInformation($"Skipping import step: {ImportSteps.Submissions}");
			}

			if (!_cache.TryGetValue(ImportSteps.Publications, out bool _))
			{
				Run("Publications", () => PublicationImporter.Import(_db, _legacySiteDb, userIdMapping));
				Run("PublicationUrls", () => PublicationUrlImporter.Import(_legacySiteDb));
				Run("Publication Ratings", () => PublicationRatingImporter.Import(_legacySiteDb, userIdMapping));
				Run("Publication Flags", () => PublicationFlagImporter.Import(_legacySiteDb));
				Run("Publication Tags", () => PublicationTagImporter.Import(_db, _legacySiteDb));
				Run("Published Author Generator", () => PublishedAuthorGenerator.Generate(_db));
				Run("Publication Maintenance Logs", () => PublicationMaintenanceLogImporter.Import(_legacySiteDb, userIdMapping));
				_cache.Set(ImportSteps.Publications, true, Durations.OneDayInSeconds);
			}
			else
			{
				_logger.LogInformation($"Skipping import step: {ImportSteps.Publications}");
			}

			if (!_cache.TryGetValue(ImportSteps.UserFiles, out bool _))
			{
				Run("User files", () => UserFileImporter.Import(_legacySiteDb, userIdMapping));
				_cache.Set(ImportSteps.UserFiles, true, Durations.OneDayInSeconds);
			}
			else
			{
				_logger.LogInformation($"Skipping import step: {ImportSteps.UserFiles}");
			}

			var elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
			stopwatch.Stop();
			_logger.LogInformation($"Import finished. Total time: {elapsedMilliseconds / 1000.0} seconds");
			_logger.LogInformation("Import breakdown:");
			foreach ((var key, long value) in ImportDurations)
			{
				_logger.LogInformation($"{key}: {value}");
			}
		}

		private IReadOnlyDictionary<int, int> RunUserImport()
		{
			IReadOnlyDictionary<int, int> result;
			var stopwatch = Stopwatch.StartNew();
			try
			{
				_logger.LogInformation("Beginning User import");
				result = UserImporter.Import(_db, _legacySiteDb, _legacyForumDb);
			}
			catch (Exception ex)
			{
				throw new ImportException("User", ex);
			}
			finally
			{
				var elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
				_logger.LogInformation($"Exited User import {elapsedMilliseconds}");
				ImportDurations.Add("User import", elapsedMilliseconds);
				stopwatch.Stop();
			}

			return result;
		}

		private void Run(string name, Action import)
		{
			var stopwatch = Stopwatch.StartNew();
			try
			{
				_logger.LogInformation($"Beginning {name} import");
				import();
			}
			catch (Exception ex)
			{
				throw new ImportException(name, ex);
			}
			finally
			{
				var elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
				_logger.LogInformation($"Exited {name} import, Total time: {elapsedMilliseconds}");
				ImportDurations.Add($"{name} import", elapsedMilliseconds);
				stopwatch.Stop();
			}
		}

		public class ImportException : Exception
		{
			public ImportException(string importStep, Exception innerException)
				: base($"Exception at import step: {importStep}", innerException)
			{
				ImportStep = importStep;
			}

			public string ImportStep { get; }
		}
	}
}
