﻿using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

using TASVideos.Models;

namespace TASVideos.Pages.Roles.Models
{
	/// <summary>
	/// Represents a Role entry for the purpose of editing
	/// </summary>
	public class RoleEditModel
	{
		[Required]
		[StringLength(50)]
		[Display(Name = "Name")]
		public string Name { get; set; } = "";

		[Display(Name = "Default", Description = "Default roles are given to all new users when they register")]
		public bool IsDefault { get; set; }

		[Required]
		[StringLength(200)]
		public string Description { get; set; } = "";

		[Display(Name = "Auto-assign on Post Count", Description = "If set, the user will automatically be assigned this role when they reach this post count.")]
		public int? AutoAssignPostCount { get; set; }

		[AtLeastOne(ErrorMessage = "At least one permission is required.")]
		[Display(Name = "Selected Permissions")]
		public IEnumerable<int> SelectedPermissions { get; set; } = new List<int>();

		[Display(Name = "Selected Assignable Permissions")]
		public IEnumerable<int> SelectedAssignablePermissions { get; set; } = new List<int>();

		[Display(Name = "Related Links")]
		public IEnumerable<string> Links { get; set; } = new List<string>();
	}
}
