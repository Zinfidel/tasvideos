﻿using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using TASVideos.Core.Services;
using TASVideos.Core.Services.ExternalMediaPublisher;
using TASVideos.Data;
using TASVideos.Data.Entity;
using TASVideos.Extensions;
using TASVideos.Pages.Wiki.Models;

namespace TASVideos.Pages.Wiki
{
	[RequireEdit]
	public class EditModel : BasePageModel
	{
		private readonly IWikiPages _wikiPages;
		private readonly ApplicationDbContext _db;
		private readonly ExternalMediaPublisher _publisher;

		public EditModel(
			IWikiPages wikiPages,
			ApplicationDbContext db,
			ExternalMediaPublisher publisher)
		{
			_wikiPages = wikiPages;
			_db = db;
			_publisher = publisher;
		}

		[FromQuery]
		public string? Path { get; set; }

		[BindProperty]
		public WikiEditModel PageToEdit { get; set; } = new ();

		public int? Id { get; set; }

		public async Task<IActionResult> OnGet()
		{
			Path = Path?.Trim('/');
			if (string.IsNullOrWhiteSpace(Path))
			{
				return NotFound();
			}

			if (!WikiHelper.IsValidWikiPageName(Path))
			{
				return NotFound();
			}

			if (WikiHelper.IsHomePage(Path) && !await UserNameExists(Path))
			{
				return NotFound();
			}

			var page = await _wikiPages.Page(Path);

			PageToEdit = new WikiEditModel
			{
				Markup = page?.Markup ?? ""
			};
			Id = page?.Id;

			return Page();
		}

		public async Task<IActionResult> OnPost()
		{
			Path = Path?.Trim('/');
			if (string.IsNullOrWhiteSpace(Path))
			{
				return NotFound();
			}

			if (!WikiHelper.IsValidWikiPageName(Path))
			{
				return Home();
			}

			if (WikiHelper.IsHomePage(Path) && !await UserNameExists(Path))
			{
				return Home();
			}

			if (!ModelState.IsValid)
			{
				return Page();
			}

			var page = new WikiPage
			{
				CreateTimestamp = PageToEdit.EditStart,
				PageName = Path.Trim('/'),
				Markup = PageToEdit.Markup,
				MinorEdit = PageToEdit.MinorEdit,
				RevisionMessage = PageToEdit.RevisionMessage,
				AuthorId = User.GetUserId()
			};
			var result = await _wikiPages.Add(page);
			if (!result)
			{
				ModelState.AddModelError("", "Unable to save. The content on this page may have been modified by another user.");
				return Page();
			}

			if (page.Revision == 1 || !PageToEdit.MinorEdit)
			{
				await _publisher.SendGeneralWiki(
					$"Page {Path} {(page.Revision > 1 ? "edited" : "created")}",
					$"({PageToEdit.RevisionMessage}): ",
					Path,
					User.Name());
			}

			return BaseRedirect("/" + page.PageName);
		}

		private async Task<bool> UserNameExists(string path)
		{
			var userName = WikiHelper.ToUserName(path);
			return await _db.Users.Exists(userName);
		}
	}
}
