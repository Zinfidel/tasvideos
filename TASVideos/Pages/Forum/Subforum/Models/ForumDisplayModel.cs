﻿using System;
using System.ComponentModel.DataAnnotations;
using TASVideos.Core;
using TASVideos.Data.Entity.Forum;

namespace TASVideos.Pages.Forum.Subforum.Models
{
	public class ForumDisplayModel
	{
		public int Id { get; set; }
		public string Name { get; set; } = "";
		public string? Description { get; set; }

		public PageOf<ForumTopicEntry> Topics { get; set; } = PageOf<ForumTopicEntry>.Empty();

		public class ForumTopicEntry
		{
			public int Id { get; set; }
			public string Title { get; set; } = "";

			[Display(Name = "Author")]
			public string? CreateUserName { get; set; }

			[Sortable]
			public DateTime CreateTimestamp { get; set; }

			public int PostCount { get; set; }

			public ForumTopicType Type { get; set; }

			public ForumPost? LastPost { get; set; }
			public DateTime LastPostDateTime => LastPost?.CreateTimestamp ?? DateTime.UtcNow; // This will never actually be null, EF just requires a nullable DateTime for .Max() operations
			public int? LastPostId => LastPost?.Id ?? 0;
			public string? LastPostUserName => LastPost?.CreateUserName ?? string.Empty;
		}
	}
}
