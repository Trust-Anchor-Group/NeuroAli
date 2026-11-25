using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Xsl;
using Waher.Networking.HTTP;
using Waher.Script;
using Waher.Security;

namespace NeuroAli
{
	/// <summary>
	/// User of the local administration pages.
	/// </summary>
	internal sealed class LocalAdminUser : IUser
	{
		public static readonly LocalAdminUser Instance = new();

		public string UserName => "LocalAdmin";
		public string FederatedUserName => this.UserName;
		public string FriendlyName => "Local administrator";
		public string PasswordHash => string.Empty;
		public string PasswordHashType => string.Empty;
		public bool HasPrivilege(string Privilege) => true;
	}

	/// <summary>
	/// Only accepts requests made from the local machine to a local host name. The host
	/// name check protects against DNS rebinding, where a web page on another site uses a
	/// domain name that resolves to 127.0.0.1.
	/// </summary>
	public sealed class LocalOnlyAuthentication : HttpAuthenticationScheme
	{
		/// <summary>
		/// Display name of scheme.
		/// </summary>
		public override string DisplayName => "Local access only";

		/// <summary>
		/// No challenges: access cannot be obtained by authenticating.
		/// </summary>
		public override string[] GetChallenges(HttpRequest Request) => [];

		/// <summary>
		/// Checks if the request comes from the local machine.
		/// </summary>
		public override Task<IUser> IsAuthenticated(HttpRequest Request)
		{
			return Task.FromResult<IUser>(AdminPages.IsLocalRequest(Request) ? LocalAdminUser.Instance : null!);
		}
	}

	/// <summary>
	/// Information about a sniffer file, as shown on the sniffer page.
	/// </summary>
	/// <param name="Category">Category (MCP, JSON-RPC or HTTP).</param>
	/// <param name="Name">File name, relative to the category folder.</param>
	/// <param name="Size">Size in bytes.</param>
	/// <param name="LastWrite">When the file was last written (local time).</param>
	/// <param name="Url">URL of the viewer for the file.</param>
	public sealed record SnifferFileInfo(string Category, string Name, long Size, DateTime LastWrite, string Url);

	/// <summary>
	/// Helpers used by the script on the administration pages in Root/Admin.
	/// </summary>
	public static class AdminPages
	{
		private const string CsrfVariable = "NeuroAliCsrfToken";
		private const int MaxSnifferFiles = 500;

		private static string workingFolder = string.Empty;
		private static string executableFolder = string.Empty;
		private static XslCompiledTransform? snifferTransform = null;

		/// <summary>
		/// Sets the folders used by the administration pages.
		/// </summary>
		internal static void Initialize(string WorkingFolder, string ExecutableFolder)
		{
			workingFolder = Path.GetFullPath(WorkingFolder);
			executableFolder = Path.GetFullPath(ExecutableFolder);
		}

		/// <summary>
		/// Checks if a request comes from the local machine, to a local host name.
		/// </summary>
		public static bool IsLocalRequest(HttpRequest Request)
		{
			if (!IPEndPoint.TryParse(Request.RemoteEndPoint ?? string.Empty, out IPEndPoint? Remote) ||
				!IPAddress.IsLoopback(Remote.Address))
			{
				return false;
			}

			string Host = Request.Header.Host?.Value ?? string.Empty;

			if (Host.StartsWith('['))
			{
				int i = Host.IndexOf(']');
				Host = i > 0 ? Host[1..i] : Host;
			}
			else
			{
				int i = Host.LastIndexOf(':');
				if (i >= 0)
					Host = Host[..i];
			}

			return string.Equals(Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
				(IPAddress.TryParse(Host, out IPAddress? Address) && IPAddress.IsLoopback(Address));
		}

		/// <summary>
		/// Encodes text for use in HTML (element content or attribute values) on a Markdown
		/// page. Text inserted by script is parsed as Markdown afterwards, so characters with
		/// a meaning in Markdown or script (for example _, *, :, { and }) are also encoded as
		/// character references. This keeps the text as-is, prevents automatic links, and
		/// prevents inserted text from being executed as script.
		/// </summary>
		/// <param name="Text">Text to encode.</param>
		/// <returns>Encoded text.</returns>
		public static string Encode(object? Text)
		{
			string s = Text?.ToString() ?? string.Empty;
			StringBuilder sb = new(s.Length + 16);

			foreach (char ch in s)
			{
				if (char.IsLetterOrDigit(ch) || ch == ' ' || ch == '.' || ch == ',' || ch == '-' || ch == '/' || ch == '?' || ch == '=' || ch == ';')
					sb.Append(ch);
				else if (ch == '\r' || ch == '\n' || ch == '\t')
					sb.Append(' ');
				else if (char.IsControl(ch))
					continue;
				else
				{
					sb.Append("&#");
					sb.Append(((int)ch).ToString());
					sb.Append(';');
				}
			}

			return sb.ToString();
		}

		/// <summary>
		/// Gets the anti-forgery token of the current session, creating one if needed.
		/// Forms include it, and posted data is only accepted if it matches.
		/// </summary>
		public static string GetCsrfToken(HttpRequest Request)
		{
			SessionVariables? Session = Request.Session;
			if (Session is null)
				return string.Empty;

			if (Session.TryGetVariable(CsrfVariable, out Variable? v) && v.ValueObject is string Token &&
				!string.IsNullOrEmpty(Token))
			{
				return Token;
			}

			// Hexadecimal, so the token has no characters with a meaning in Markdown.
			Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
			Session[CsrfVariable] = Token;

			return Token;
		}

		/// <summary>
		/// Checks the anti-forgery token of posted form data.
		/// </summary>
		/// <param name="Request">Current request.</param>
		/// <param name="Token">Token received in the posted data.</param>
		/// <returns>If the token matches the session's token.</returns>
		public static bool IsValidCsrfToken(HttpRequest Request, object? Token)
		{
			SessionVariables? Session = Request.Session;

			if (Session is null || Token is not string Received ||
				!Session.TryGetVariable(CsrfVariable, out Variable? v) || v.ValueObject is not string Expected ||
				string.IsNullOrEmpty(Expected))
			{
				return false;
			}

			return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Received),
				Encoding.UTF8.GetBytes(Expected));
		}

		private static (string Category, string Folder)[] SnifferFolders =>
		[
			("MCP", Path.Combine(workingFolder, "MCP", "Sniffers")),
			("JSON-RPC", Path.Combine(workingFolder, "JSON-RPC")),
			("HTTP", Path.Combine(workingFolder, "HTTP"))
		];

		/// <summary>
		/// Lists sniffer files, most recently written first.
		/// </summary>
		/// <param name="Category">Category to list, or empty for all.</param>
		public static SnifferFileInfo[] GetSnifferFiles(string? Category)
		{
			List<SnifferFileInfo> Result = [];

			foreach ((string Category2, string Folder) in SnifferFolders)
			{
				if (!string.IsNullOrEmpty(Category) &&
					!string.Equals(Category, Category2, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				if (!Directory.Exists(Folder))
					continue;

				foreach (string FileName in Directory.EnumerateFiles(Folder, "*.xml", SearchOption.AllDirectories))
				{
					FileInfo Info = new(FileName);
					string Name = Path.GetRelativePath(Folder, FileName).Replace('\\', '/');

					Result.Add(new SnifferFileInfo(Category2, Name, Info.Length, Info.LastWriteTime,
						"/Admin/Sniffer?c=" + Uri.EscapeDataString(Category2) + "&f=" + Uri.EscapeDataString(Name)));
				}
			}

			Result.Sort((a, b) => b.LastWrite.CompareTo(a.LastWrite));

			if (Result.Count > MaxSnifferFiles)
				Result.RemoveRange(MaxSnifferFiles, Result.Count - MaxSnifferFiles);

			return [.. Result];
		}

		/// <summary>
		/// Gets the full path of a sniffer file, making sure it lies inside a sniffer folder.
		/// </summary>
		internal static bool TryGetSnifferFile(string? Category, string? Name, out string FullPath)
		{
			FullPath = string.Empty;

			if (string.IsNullOrEmpty(Category) || string.IsNullOrEmpty(Name) ||
				!Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			foreach ((string Category2, string Folder) in SnifferFolders)
			{
				if (!string.Equals(Category, Category2, StringComparison.OrdinalIgnoreCase))
					continue;

				string Root = Path.GetFullPath(Folder) + Path.DirectorySeparatorChar;
				string Candidate = Path.GetFullPath(Path.Combine(Root, Name.Replace('/', Path.DirectorySeparatorChar)));

				if (!Candidate.StartsWith(Root, StringComparison.OrdinalIgnoreCase) || !File.Exists(Candidate))
					return false;

				FullPath = Candidate;
				return true;
			}

			return false;
		}

		/// <summary>
		/// Renders a sniffer file as HTML, using Transforms/SnifferXmlToHtml.xslt.
		/// </summary>
		internal static async Task<string> RenderSnifferAsync(string FullPath)
		{
			string Xml;

			// The file may be open for writing by the sniffer.
			await using (FileStream f = new(FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
			{
				using StreamReader r = new(f, Encoding.UTF8);
				Xml = await r.ReadToEndAsync();
			}

			// Files still being written have no closing root tag yet.
			if (!Xml.TrimEnd().EndsWith("</SnifferOutput>", StringComparison.Ordinal))
			{
				int i = Xml.LastIndexOf('>');
				if (i >= 0)
					Xml = Xml[..(i + 1)];

				Xml += "\r\n</SnifferOutput>";
			}

			XslCompiledTransform Transform = snifferTransform ??= LoadTransform(
				Path.Combine(executableFolder, "Transforms", "SnifferXmlToHtml.xslt"));

			XmlReaderSettings Settings = new()
			{
				DtdProcessing = DtdProcessing.Prohibit,
				XmlResolver = null
			};

			using StringReader Input = new(Xml);
			using XmlReader Reader = XmlReader.Create(Input, Settings);
			using StringWriter Output = new();

			Transform.Transform(Reader, null, Output);
			return Output.ToString();
		}

		private static XslCompiledTransform LoadTransform(string FileName)
		{
			XslCompiledTransform Transform = new();
			Transform.Load(FileName, XsltSettings.Default, null);
			return Transform;
		}
	}

	/// <summary>
	/// Shows a sniffer file as HTML: /Admin/Sniffer?c=CATEGORY&amp;f=FILE
	/// </summary>
	public sealed class SnifferViewerResource : HttpSynchronousResource, IHttpGetMethod
	{
		private readonly HttpAuthenticationScheme[] authenticationSchemes;

		/// <summary>
		/// Shows a sniffer file as HTML.
		/// </summary>
		/// <param name="ResourceName">Resource name.</param>
		/// <param name="AuthenticationSchemes">Authentication schemes.</param>
		public SnifferViewerResource(string ResourceName, params HttpAuthenticationScheme[] AuthenticationSchemes)
			: base(ResourceName)
		{
			this.authenticationSchemes = AuthenticationSchemes;
		}

		/// <inheritdoc/>
		public override bool HandlesSubPaths => false;

		/// <inheritdoc/>
		public override bool UserSessions => false;

		/// <inheritdoc/>
		public bool AllowsGET => true;

		/// <inheritdoc/>
		public override HttpAuthenticationScheme[] GetAuthenticationSchemes(HttpRequest Request)
		{
			return this.authenticationSchemes;
		}

		/// <inheritdoc/>
		public async Task GET(HttpRequest Request, HttpResponse Response)
		{
			string? Category = null;
			string? Name = null;

			if (Request.Header.QueryParameters is not null)
			{
				foreach (KeyValuePair<string, string> P in Request.Header.QueryParameters)
				{
					switch (P.Key)
					{
						case "c": Category = P.Value; break;
						case "f": Name = P.Value; break;
					}
				}
			}

			if (!AdminPages.TryGetSnifferFile(Category, Name, out string FullPath))
				throw new NotFoundException("Sniffer file not found.");

			string Html = await AdminPages.RenderSnifferAsync(FullPath);

			Response.ContentType = "text/html; charset=utf-8";
			Response.SetHeader("Cache-Control", "no-store");
			await Response.Write(Html);
		}
	}
}
