Title: XMPP account
Description: The XMPP account Neuro-Ali uses.
CSS: /Admin/Admin.css
Cache-Control: max-age=0, no-cache, no-store

<nav><a href="/Admin/Remotes.md">Remote servers</a><a href="/Admin/Xmpp.md">XMPP account</a><a href="/Admin/Sniffers.md">Sniffers</a></nav>

XMPP account
===============

Neuro-Ali uses this account for the XMPP, Identity and Payments tools, and to request
tokens for remote MCP servers. If no account is set, Neuro-Ali asks for one the first time
it is needed.

{{
NA:=NeuroAli;

if exists(Posted) then
(
	if !NA.AdminPages.IsValidCsrfToken(Request,Posted.Csrf) then
		]]<div class='error'>The form has expired. Reload the page and try again.</div>[[
	else if Posted.Action="Save" then
	(
		try
		(
			Saved:=NA.XmppAccountSettings.SaveAsync(Posted.Account,Posted.Domain,Posted.Password,
				Posted.TrustServer ??? false,Posted.AllowInsecure ??? false);
			]]<div class='result'>Logged in as <strong>((NA.AdminPages.Encode(Saved.BareJid) ))</strong>. The account is saved and used right away.</div>[[
		)
		catch
		(
			]]<div class='error'>((NA.AdminPages.Encode(Exception.Message) ))</div>[[
		)
	)
	else if Posted.Action="Remove" then
	(
		if NA.XmppAccountSettings.RemoveAsync() then
			]]<div class='result'>The saved account was removed. If Neuro-Ali is connected, the connection stays until it restarts.</div>[[
		else
			]]<div class='error'>No account was saved.</div>[[
	)
	else if Posted.Action="SendPresence" or Posted.Action="AcceptPresence" or Posted.Action="AcceptAndRequest" then
	(
		try
		(
			ContactJid:=Posted.ContactJid ??? "";
			if Posted.Action="SendPresence" then
			(
				NA.XmppAccountSettings.SendPresenceRequestAsync(ContactJid);
				]]<div class='result'>Sent a presence request to <strong>((NA.AdminPages.Encode(ContactJid) ))</strong>.</div>[[
			)
			else
			(
				RequestBack:=Posted.Action="AcceptAndRequest";
				NA.XmppAccountSettings.AcceptPresenceRequestAsync(ContactJid,RequestBack);
				Text:=if RequestBack then "Request accepted and a request sent back." else "Request accepted.";
				]]<div class='result'>((Text))</div>[[
			)
		)
		catch
		(
			]]<div class='error'>((NA.AdminPages.Encode(Exception.Message) ))</div>[[
		)
	)
);

Csrf:=NA.AdminPages.GetCsrfToken(Request);
Current:=NA.XmppAccountSettings.GetAsync();

if exists(Current.Account) then
(
	Html:="<table><tbody>"+
		"<tr><th>Account</th><td><code>"+NA.AdminPages.Encode(Current.BareJid)+"</code></td></tr>"+
		"<tr><th>Trust server certificate</th><td>"+(if Current.TrustServer then "Yes" else "No")+"</td></tr>"+
		"<tr><th>Insecure login mechanisms</th><td>"+(if Current.AllowInsecureMechanisms then "Allowed" else "Not allowed")+"</td></tr>"+
		"</tbody></table>"+
		"<form action='Xmpp.md' method='post'>"+
		"<input type='hidden' name='Csrf' value='"+NA.AdminPages.Encode(Csrf)+"'/>"+
		"<input type='hidden' name='Action' value='Remove'/>"+
		"<button type='submit'>Remove saved account</button></form>";
	]]((Html))[[
)
else
	]]<p>No account is saved.</p>[[
}}

{{
if exists(Current.Account) then
(
	]]<h2>Presence subscriptions</h2>[[;
	try
	(
		Requests:=NA.XmppAccountSettings.GetPresenceRequestsAsync();
		if count(Requests)=0 then
			]]<p>No incoming presence requests.</p>[[
		else
		(
			Html:="<table><thead><tr><th>Incoming request from</th><th>Actions</th></tr></thead><tbody>";
			foreach Jid in Requests do
			(
				Contact:=NA.AdminPages.Encode(Jid);
				Html+="<tr><td><code>"+Contact+"</code></td><td><form action='Xmpp.md' method='post'>"+
					"<input type='hidden' name='Csrf' value='"+NA.AdminPages.Encode(Csrf)+"'/>"+
					"<input type='hidden' name='ContactJid' value='"+Contact+"'/>"+
					"<button type='submit' name='Action' value='AcceptPresence'>Accept</button> "+
					"<button type='submit' name='Action' value='AcceptAndRequest'>Accept and send request back</button>"+
					"</form></td></tr>"
			);
			Html+="</tbody></table>";
			]]((Html))[[
		)
	)
	catch
	(
		]]<div class='error'>((NA.AdminPages.Encode(Exception.Message) ))</div>[[
	);
	]]<form action="Xmpp.md" method="post">
	<fieldset>
	<legend>Send a presence request</legend>
	<input type="hidden" name="Csrf" value="((NA.AdminPages.Encode(Csrf) ))"/>
	<input type="hidden" name="Action" value="SendPresence"/>
	<label for="ContactJid">Contact XMPP address</label>
	<input type="text" id="ContactJid" name="ContactJid" required placeholder="name@neuron.example"/>
	<button type="submit">Send request</button>
	</fieldset>
	</form>[[
);
}}

<form action="Xmpp.md" method="post" autocomplete="off">
<fieldset>
<legend>Set account</legend>
<input type="hidden" name="Csrf" value="{{Csrf}}"/>
<input type="hidden" name="Action" value="Save"/>

<label for="Account">Account</label>
<input type="text" id="Account" name="Account" required placeholder="ali or ali@neuron.example"/>

<label for="Domain">Domain</label>
<input type="text" id="Domain" name="Domain" placeholder="neuron.example"/>
<span class="hint">Can be left empty if the account is given as account@domain.</span>

<label for="Password">Password</label>
<input type="password" id="Password" name="Password" required autocomplete="new-password"/>
<span class="hint">Neuro-Ali logs in to check the password. Only the resulting password hash is saved.</span>

<label><input type="checkbox" name="TrustServer"/> Trust the server certificate even if it is invalid</label>
<label><input type="checkbox" name="AllowInsecure"/> Allow insecure login mechanisms (PLAIN, DIGEST-MD5, CRAM-MD5)</label>

<button type="submit">Log in and save</button>
</fieldset>
</form>

If Neuro-Ali is already connected with another account, saving removes the saved account,
but the connection stays until Neuro-Ali restarts. Restart it and set the account here
before any XMPP tool is used.
