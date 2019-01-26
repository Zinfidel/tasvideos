using System.Linq;
using System.Security.Claims;

namespace TASVideos.Extensions
{
	public static class ClaimsPrincipalExtensions
	{
		public static int GetUserId(this ClaimsPrincipal user)
		{
			if (user == null || !user.Identity.IsAuthenticated)
			{
				return 0;
			}

			return int.Parse(user.Claims
				.First(c => c.Type == ClaimTypes.NameIdentifier).Value);
		}
	}
}
