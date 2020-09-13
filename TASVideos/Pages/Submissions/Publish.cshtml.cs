﻿using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

using AutoMapper.QueryableExtensions;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

using TASVideos.Data;
using TASVideos.Data.Entity;
using TASVideos.Extensions;
using TASVideos.Pages.Submissions.Models;
using TASVideos.Services;
using TASVideos.Services.ExternalMediaPublisher;

namespace TASVideos.Pages.Submissions
{
	[RequirePermission(PermissionTo.PublishMovies)]
	public class PublishModel : BasePageModel
	{
		private readonly ApplicationDbContext _db;
		private readonly ExternalMediaPublisher _publisher;
		private readonly IWikiPages _wikiPages;
		private readonly IMediaFileUploader _uploader;
		private readonly ITASVideoAgent _tasVideosAgent;

		public PublishModel(
			ApplicationDbContext db,
			ExternalMediaPublisher publisher,
			IWikiPages wikiPages,
			IMediaFileUploader uploader,
			ITASVideoAgent tasVideoAgent)
		{
			_db = db;
			_publisher = publisher;
			_wikiPages = wikiPages;
			_uploader = uploader;
			_tasVideosAgent = tasVideoAgent;
		}

		[FromRoute]
		public int Id { get; set; }

		[BindProperty]
		public SubmissionPublishModel Submission { get; set; } = new SubmissionPublishModel();

		public IEnumerable<SelectListItem> AvailableMoviesToObsolete { get; set; } = new List<SelectListItem>();

		public async Task<IActionResult> OnGet()
		{
			Submission = await _db.Submissions
				.Where(s => s.Id == Id)
				.ProjectTo<SubmissionPublishModel>()
				.SingleOrDefaultAsync();

			if (Submission == null)
			{
				return NotFound();
			}

			if (!Submission.CanPublish)
			{
				return AccessDenied();
			}

			await PopulateAvailableMoviesToObsolete(Submission.SystemId);

			return Page();
		}

		public async Task<IActionResult> OnPost()
		{
			if (!Submission.Screenshot.IsValidImage())
			{
				ModelState.AddModelError($"{nameof(Submission)}.{nameof(Submission.Screenshot)}", "Invalid file type. Must be .png or .jpg");
			}

			if (!Submission.TorrentFile.IsValidTorrent())
			{
				ModelState.AddModelError($"{nameof(Submission)}.{nameof(Submission.TorrentFile)}", "Invalid file type. Must be a .torrent file");
			}

			if (!ModelState.IsValid)
			{
				await PopulateAvailableMoviesToObsolete(Submission.SystemId);
				return Page();
			}

			// TODO: I think this is producing joins, if the submission isn't properly cataloged then 
			// this will throw an exception, if so, use OrDefault and return NotFound()
			// if it is doing left joins or sub-queries, then we need to null check the usages of nullable 
			// tables such as game, rom, etc and throw if those are null
			var submission = await _db.Submissions
				.Include(s => s.IntendedTier)
				.Include(s => s.System)
				.Include(s => s.SystemFrameRate)
				.Include(s => s.Game)
				.Include(s => s.Rom)
				.Include(s => s.SubmissionAuthors)
				.ThenInclude(sa => sa.Author)
				.SingleAsync(s => s.Id == Id);

			var publication = new Publication
			{
				TierId = submission.IntendedTier!.Id,
				SystemId = submission.System!.Id,
				SystemFrameRateId = submission.SystemFrameRate!.Id,
				GameId = submission.Game!.Id,
				RomId = submission.Rom!.Id,
				Branch = submission.Branch,
				EmulatorVersion = Submission.EmulatorVersion,
				OnlineWatchingUrl = Submission.OnlineWatchingUrl,
				MirrorSiteUrl = Submission.MirrorSiteUrl,
				Frames = submission.Frames,
				RerecordCount = submission.RerecordCount,
				MovieFileName = Submission.MovieFileName + "." + Submission.MovieExtension
			};

			// TODO: use IFileService for this
			// Unzip the submission file, and re-zip it while renaming the contained file
			await using (var publicationFileStream = new MemoryStream())
			{
				using (var publicationZipArchive = new ZipArchive(publicationFileStream, ZipArchiveMode.Create))
				{
					await using var submissionFileStream = new MemoryStream(submission.MovieFile);
					using var submissionZipArchive = new ZipArchive(submissionFileStream, ZipArchiveMode.Read);
					var publicationZipEntry = publicationZipArchive.CreateEntry(Submission.MovieFileName + "." + Submission.MovieExtension);
					var submissionZipEntry = submissionZipArchive.Entries.Single();

					await using var publicationZipEntryStream = publicationZipEntry.Open();
					await using var submissionZipEntryStream = submissionZipEntry.Open();
					await submissionZipEntryStream.CopyToAsync(publicationZipEntryStream);
				}

				publication.MovieFile = publicationFileStream.ToArray();
			}

			var publicationAuthors = submission.SubmissionAuthors
				.Select(sa => new PublicationAuthor
				{
					Publication = publication,
					Author = sa.Author
				});

			foreach (var author in publicationAuthors)
			{
				publication.Authors.Add(author);
			}

			publication.Submission = submission;
			_db.Publications.Add(publication);

			await _db.SaveChangesAsync(); // Need an Id for the Title
			publication.GenerateTitle();

			await _uploader.UploadScreenshot(publication.Id, Submission.Screenshot!, Submission.ScreenshotDescription);
			await _uploader.UploadTorrent(publication.Id, Submission.TorrentFile!);

			// Create a wiki page corresponding to this submission
			var wikiPage = new WikiPage
			{
				RevisionMessage = $"Auto-generated from Movie #{publication.Id}",
				PageName = LinkConstants.PublicationWikiPage + publication.Id,
				MinorEdit = false,
				Markup = Submission.MovieMarkup
			};

			await _wikiPages.Add(wikiPage);
			publication.WikiContent = wikiPage;

			submission.Status = SubmissionStatus.Published;
			var history = new SubmissionStatusHistory
			{
				SubmissionId = submission.Id,
				Status = SubmissionStatus.Published
			};
			submission.History.Add(history);
			_db.SubmissionStatusHistory.Add(history);

			if (Submission.MovieToObsolete.HasValue)
			{
				var toObsolete = await _db.Publications.SingleAsync(p => p.Id == Submission.MovieToObsolete);
				toObsolete.ObsoletedById = publication.Id;
			}

			await _db.SaveChangesAsync();

			await _tasVideosAgent.PostSubmissionPublished(submission.Id, publication.Id);
			_publisher.AnnouncePublication(publication.Title, $"{publication.Id}M", $"{User.Identity.Name}");

			return Redirect($"/{publication.Id}M");
		}

		private async Task PopulateAvailableMoviesToObsolete(int systemId)
		{
			AvailableMoviesToObsolete = await _db.Publications
				.ThatAreCurrent()
				.Where(p => p.SystemId == systemId)
				.Select(p => new SelectListItem
				{
					Value = p.Id.ToString(),
					Text = p.Title
				})
				.ToListAsync();
		}
	}
}
