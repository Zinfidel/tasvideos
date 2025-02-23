﻿using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using TASVideos.Core.Services;
using TASVideos.Core.Services.ExternalMediaPublisher;
using TASVideos.Data.Entity;

namespace TASVideos.Pages.Submissions
{
	[RequirePermission(PermissionTo.DeprecateMovieParsers)]
	public class DeprecateMovieFormatsModel : BasePageModel
	{
		private readonly IMovieFormatDepcrecator _depcrecator;
		private readonly ExternalMediaPublisher _publisher;

		public IReadOnlyDictionary<string, DeprecatedMovieFormat?> MovieExtensions { get; set; } = new Dictionary<string, DeprecatedMovieFormat?>();

		public DeprecateMovieFormatsModel(
			IMovieFormatDepcrecator depcrecator,
			ExternalMediaPublisher publisher)
		{
			_depcrecator = depcrecator;
			_publisher = publisher;
		}

		public async Task OnGet()
		{
			MovieExtensions = await _depcrecator.GetAll();
		}

		public async Task<IActionResult> OnPost(string extension, bool deprecate)
		{
			if (!_depcrecator.IsMovieExtension(extension))
			{
				return BadRequest($"Invalid format {extension}");
			}

			var result = deprecate
				? await _depcrecator.Deprecate(extension)
				: await _depcrecator.Allow(extension);

			if (result)
			{
				SuccessStatusMessage($"{extension} allowed successfully");
				await SendAnnouncement(extension, deprecate);
			}
			else
			{
				ErrorStatusMessage("Unable to save");
			}

			return BasePageRedirect("DeprecateMovieFormats");
		}

		private async Task SendAnnouncement(string extension, bool deprecate)
		{
			await _publisher.SendSubmissionEdit($"{extension} depcrecated status set to {deprecate}", "", User.Name());
		}
	}
}
