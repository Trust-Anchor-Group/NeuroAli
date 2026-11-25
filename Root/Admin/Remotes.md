Title: Remote MCP servers
Description: Remote MCP endpoints proxied by Neuro-Ali.
CSS: /Admin/Admin.css
Cache-Control: max-age=0, no-cache, no-store

<nav><a href="/Admin/Remotes.md">Remote servers</a><a href="/Admin/Xmpp.md">XMPP account</a><a href="/Admin/Sniffers.md">Sniffers</a></nav>

Remote MCP servers
=====================

Neuro-Ali proxies these MCP endpoints. Their tools and prompts are listed as `ALIAS_NAME`.
Discover servers from a Neuron address, then enable the ones you want to expose to your MCP client.

{{
NA:=NeuroAli;

if exists(Posted) then
(
	if !NA.AdminPages.IsValidCsrfToken(Request,Posted.Csrf) then
		]]<div class='error'>The form has expired. Reload the page and try again.</div>[[
	else if Posted.Action="Discover" then
	(
		try
		(
			Found:=NA.RemoteMcpConfiguration.DiscoverAsync(Posted.BaseUrl,Posted.Issuer ??? "");
			]]<div class='result'>Accessible servers found: ((Found)). New servers start disabled.</div>[[
		)
		catch
		(
			]]<div class='error'>((NA.AdminPages.Encode(Exception.Message) ))</div>[[
		)
	)
	else if Posted.Action="Enable" or Posted.Action="Disable" then
	(
		try
		(
			NA.RemoteMcpConfiguration.SetEnabledAsync(Posted.Alias,Posted.Action="Enable");
			]]<div class='result'>Remote server settings updated.</div>[[
		)
		catch
		(
			]]<div class='error'>((NA.AdminPages.Encode(Exception.Message) ))</div>[[
		)
	)
	else if Posted.Action="Add" then
	(
		try
		(
			NA.RemoteMcpConfiguration.AddAsync(Posted.Alias,Posted.Url,Posted.Issuer);
			]]<div class='result'>Remote server <strong>((NA.AdminPages.Encode(Posted.Alias) ))</strong> saved. Restart Neuro-Ali to start using it.</div>[[
		)
		catch
		(
			]]<div class='error'>((NA.AdminPages.Encode(Exception.Message) ))</div>[[
		)
	)
	else if Posted.Action="Remove" then
	(
		if NA.RemoteMcpConfiguration.RemoveAsync(Posted.Alias) then
			]]<div class='result'>Remote server <strong>((NA.AdminPages.Encode(Posted.Alias) ))</strong> removed.</div>[[
		else
			]]<div class='error'>No saved remote server with that alias was found.</div>[[
	)
);

Csrf:=NA.AdminPages.GetCsrfToken(Request);
Status:=NA.RemoteMcpConfiguration.GetStatusAsync();

if count(Status)=0 then
	]]<p>No remote servers are defined yet.</p>[[
else
(
	Html:="<table><thead><tr><th>Alias</th><th>URL</th><th>JWT issuer</th><th>Defined</th>"+
		"<th>State</th><th>Tools</th><th>Last error</th><th></th></tr></thead><tbody>";

	foreach S in Status do
	(
		Html+="<tr><td><code>"+NA.AdminPages.Encode(S.Alias)+"</code></td>"+
			"<td>"+NA.AdminPages.Encode(S.Url)+"</td>"+
			"<td>"+(if S.Issuer="" then "Local XMPP broker" else NA.AdminPages.Encode(S.Issuer))+"</td>"+
			"<td>"+(if S.Source=NA.RemoteMcpSource.CommandLine then "Command line (<code>-r</code>)" else "Saved")+"</td>"+
			"<td"+(if S.Running then "" else " class='pending'")+">"+NA.AdminPages.Encode(S.State)+"</td>"+
			"<td>";

		if !exists(S.ToolNames) then
			Html+="<span class='hint'>Awaiting discovery</span>"
		else if count(S.ToolNames)=0 then
			Html+="<span class='hint'>No tools exposed</span>"
		else
		(
			Html+="<details class='remote-tools'><summary>"+count(S.ToolNames)+"</summary><ul>";
			foreach Name in S.ToolNames do
				Html+="<li><code>"+NA.AdminPages.Encode(Name)+"</code></li>";
			Html+="</ul></details>"
		);

		Html+="</td><td class='error'>"+NA.AdminPages.Encode(S.LastError)+"</td><td>";

		if S.Source=NA.RemoteMcpSource.Saved then
		(
			Html+="<form action='Remotes.md' method='post'>"+
				"<input type='hidden' name='Csrf' value='"+NA.AdminPages.Encode(Csrf)+"'/>"+
				"<input type='hidden' name='Alias' value='"+NA.AdminPages.Encode(S.Alias)+"'/>";

			if S.CanToggle then
			(
				Action:=if S.Running then "Disable" else "Enable";
				Html+="<button type='submit' name='Action' value='"+Action+"'>"+Action+"</button> "
			);

			Html+="<button type='submit' name='Action' value='Remove'>Remove</button></form>"
		);

		Html+="</td></tr>"
	);

	Html+="</tbody></table><p class='hint'>Tool counts reflect the latest successful discovery."+
		" Expand a count to see the tool names.</p>";
	]]((Html))[[
)
}}

<form action="Remotes.md" method="post">
<fieldset>
<legend>Discover servers on a Neuron</legend>
<input type="hidden" name="Csrf" value="{{Csrf}}"/>
<input type="hidden" name="Action" value="Discover"/>

<label for="BaseUrl">Neuron base URL</label>
<input type="url" id="BaseUrl" name="BaseUrl" required placeholder="https://neuron.example"/>

<label for="DiscoveryIssuer">JWT issuer (optional)</label>
<input type="text" id="DiscoveryIssuer" name="Issuer" placeholder="neuron.example"/>
<span class="hint">For a child Neuron, enter its bare JID after it approves your presence subscription. Neuro-Ali finds its online resource.</span>

<button type="submit">Discover servers</button>
</fieldset>
</form>

<form action="Remotes.md" method="post">
<fieldset>
<legend>Add remote server</legend>
<input type="hidden" name="Csrf" value="{{Csrf}}"/>
<input type="hidden" name="Action" value="Add"/>

<label for="Alias">Alias</label>
<input type="text" id="Alias" name="Alias" required pattern="[A-Za-z0-9\-]{1,16}" maxlength="16" placeholder="b"/>
<span class="hint">1-16 letters, digits or hyphens. Tools are named <code>ALIAS&#95;NAME</code>.</span>

<label for="Url">MCP endpoint URL</label>
<input type="url" id="Url" name="Url" required placeholder="https://neuron-b.example/MCP/XMPP"/>
<span class="hint">Must use HTTPS (plain HTTP is only accepted for localhost).</span>

<label for="Issuer">JWT issuer (optional)</label>
<input type="text" id="Issuer" name="Issuer" placeholder="neuron-b.example"/>
<span class="hint">Use the broker domain, the child Neuron's bare JID with an approved presence subscription, or its full JID. If empty, localhost uses the connected local broker and other hosts use the URL host name.</span>

<button type="submit">Save</button>
</fieldset>
</form>

To discover a Neuron running on this machine, connect an XMPP account on that Neuron,
enter its local URL (for example, `http://localhost`), and leave the JWT issuer blank.
Neuro-Ali requests a JWT from the broker of the connected account. The Neuron must grant
the account's bare JID the privileges its tools require.
