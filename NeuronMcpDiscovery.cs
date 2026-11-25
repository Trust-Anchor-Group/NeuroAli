using System.Text.Json;
using System.Text.Json.Nodes;
using Waher.Networking.XMPP;

namespace NeuroAli
{
	internal static class NeuronMcpDiscovery
	{
		private static readonly string[] GatewayPaths =
		[
			"/MCP", "/MCP/Content", "/MCP/EventLog", "/MCP/Files", "/MCP/Identity", "/MCP/Payments", "/MCP/XMPP"
		];

		internal static async Task<(Uri Url, string[] ToolNames)[]> FindAsync(Uri BaseUrl, string Issuer,
			XmppClient Client)
		{
			Uri[] Candidates = await GetCandidatesAsync(BaseUrl);
			(string[]? ToolNames, string? Failure)[] Results = new (string[]?, string?)[Candidates.Length];
			await Parallel.ForEachAsync(Enumerable.Range(0, Candidates.Length),
				new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (Index, Cancel) =>
				{
					Results[Index] = await ProbeAsync(Candidates[Index], Issuer, Client, Cancel);
				});

			List<(Uri Url, string[] ToolNames)> Available = new List<(Uri, string[])>();
			for (int Index = 0; Index < Results.Length; Index++)
			{
				if (Results[Index].ToolNames is string[] ToolNames)
					Available.Add((Candidates[Index], ToolNames));
			}

			if (Available.Count == 0)
			{
				string[] Failures = Results.Select(Result => Result.Failure).OfType<string>().Distinct().ToArray();
				if (Failures.Length > 0)
				{
					throw new RemoteMcpException(
						"No accessible MCP servers found. " + string.Join(" ", Failures));
				}
			}

			return Available.ToArray();
		}

		private static async Task<Uri[]> GetCandidatesAsync(Uri BaseUrl)
		{
			using HttpClient Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = !BaseUrl.IsLoopback })
			{
				Timeout = TimeSpan.FromSeconds(8),
				MaxResponseContentBufferSize = 256 * 1024
			};
			Uri MetadataUrl = new Uri(BaseUrl, "/.well-known/oauth-authorization-server");
			JsonObject? Metadata = null;

			try
			{
				using HttpResponseMessage Response = await Http.GetAsync(MetadataUrl);
				if (Response.IsSuccessStatusCode)
					Metadata = JsonNode.Parse(await Response.Content.ReadAsStringAsync()) as JsonObject;
			}
			catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
			{
				// Older deployments may not publish OAuth metadata.
			}

			if (Metadata?["mcp_servers"] is JsonArray Servers)
				return ResolveCandidates(BaseUrl, ReadStrings(Servers));

			List<string> Paths = new List<string>(GatewayPaths);
			if (Metadata?["scopes_supported"] is JsonArray Scopes)
			{
				foreach (string Scope in ReadStrings(Scopes))
				{
					string[] Segments = Scope.Split(':');
					if (Segments.Length < 3 || Segments[0] != "MCP" ||
						Segments[^1] is not ("Tools" or "Prompts" or "Resources"))
					{
						continue;
					}

					string Path = Segments[1] == "InternetContent" && Segments.Length == 3 ?
						"Content" : string.Join('/', Segments[1..^1].Select(Uri.EscapeDataString));
					Paths.Add("/MCP/" + Path);
				}
			}

			return ResolveCandidates(BaseUrl, Paths);
		}

		private static IEnumerable<string> ReadStrings(JsonArray Items)
		{
			foreach (JsonValue Item in Items.OfType<JsonValue>())
			{
				if (Item.TryGetValue(out string? Value) && !string.IsNullOrWhiteSpace(Value))
					yield return Value;
			}
		}

		private static Uri[] ResolveCandidates(Uri BaseUrl, IEnumerable<string> Paths)
		{
			HashSet<Uri> Candidates = new HashSet<Uri>();
			foreach (string Path in Paths)
			{
				if (Uri.TryCreate(BaseUrl, Path, out Uri? Url) && Url.Scheme == BaseUrl.Scheme &&
					Url.IdnHost == BaseUrl.IdnHost && Url.Port == BaseUrl.Port &&
					Url.UserInfo.Length == 0 && Url.Query.Length == 0 && Url.Fragment.Length == 0)
				{
					Candidates.Add(Url);
				}
			}

			return Candidates.OrderBy(Url => Url.AbsolutePath, StringComparer.Ordinal).ToArray();
		}

		private static async Task<(string[]? ToolNames, string? Failure)> ProbeAsync(
			Uri Url, string Issuer, XmppClient Client, CancellationToken Cancel)
		{
			using CancellationTokenSource Timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
			Timeout.CancelAfter(TimeSpan.FromSeconds(10));
			RemoteMcpEndpoint Endpoint = new RemoteMcpEndpoint("discovery", Url, Issuer);
			await using RemoteMcpServer Probe = new RemoteMcpServer(Endpoint, _ => Task.FromResult<XmppClient?>(Client),
				_ => { }, _ => { }, ListenForEvents: false);
			try
			{
				string[]? ToolNames = await Probe.DiscoverToolsAsync(Timeout.Token);
				return (ToolNames, null);
			}
			catch (OperationCanceledException) when (!Cancel.IsCancellationRequested)
			{
				return (null, Url.AbsolutePath + ": Discovery timed out.");
			}
			catch (RemoteMcpException Error)
			{
				return (null, Error.Message);
			}
			catch (HttpRequestException Error)
			{
				return (null, Url.AbsolutePath + ": HTTP request failed. " + Error.Message);
			}
			catch (JsonException)
			{
				return (null, Url.AbsolutePath + ": The response was not valid MCP JSON.");
			}
		}
	}
}
