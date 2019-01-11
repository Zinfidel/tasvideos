﻿using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TASVideos.Models;
using TASVideos.Tasks;

namespace TASVideos.Pages.Users
{
	[AllowAnonymous]
	public class ProfileModel : BasePageModel
	{
		private readonly AwardTasks _awardTasks;

		public ProfileModel(
			AwardTasks awardTasks,
			UserTasks userTasks) 
			: base(userTasks)
		{
			_awardTasks = awardTasks;
		}

		// Allows for a query based call to this page for Users/List
		[FromQuery]
		public string Name { get; set; }

		[FromRoute]
		public string UserName { get; set; }

		public UserProfileModel Profile { get; set; } = new UserProfileModel();

		public async Task<IActionResult> OnGet()
		{
			if (string.IsNullOrWhiteSpace(UserName) && string.IsNullOrWhiteSpace(Name))
			{
				return RedirectToPage("/Users/List");
			}

			if (string.IsNullOrWhiteSpace(UserName))
			{
				UserName = Name;
			}

			Profile = await UserTasks.GetUserProfile(UserName, includeHidden: false);
			if (User == null)
			{
				return NotFound();
			}

			if (!string.IsNullOrWhiteSpace(Profile.Signature))
			{
				Profile.Signature = RenderPost(Profile.Signature, true, false);
			}

			Profile.Awards = await _awardTasks.GetAllAwardsForUser(Profile.Id);
			return Page();
		}
	}
}
