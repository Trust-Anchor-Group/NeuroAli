Title: Sniffers
Description: Sniffer logs written by Neuro-Ali.
CSS: /Admin/Admin.css
Cache-Control: max-age=0, no-cache, no-store
Parameter: Category

<nav><a href="/Admin/Remotes.md">Remote servers</a><a href="/Admin/Xmpp.md">XMPP account</a><a href="/Admin/Sniffers.md">Sniffers</a></nav>

Sniffers
===========

Sniffer files record traffic, most recent first. They are kept for 7 days. Click a file to
view it. Files that are still being written show the traffic so far.

<p>
Show: <a href="/Admin/Sniffers.md">All</a> |
<a href="/Admin/Sniffers.md?Category=MCP">MCP</a> |
<a href="/Admin/Sniffers.md?Category=JSON-RPC">JSON-RPC (STDIO)</a> |
<a href="/Admin/Sniffers.md?Category=HTTP">HTTP (local web server)</a>
</p>

{{
NA:=NeuroAli;
Files:=NA.AdminPages.GetSnifferFiles(Category ??? "");

if count(Files)=0 then
	]]<p>No sniffer files found.</p>[[
else
(
	Html:="<table><thead><tr><th>Category</th><th>File</th><th>Size</th><th>Last written</th></tr></thead><tbody>";

	foreach F in Files do
	(
		Html+="<tr><td>"+NA.AdminPages.Encode(F.Category)+"</td>"+
			"<td><a href='"+NA.AdminPages.Encode(F.Url)+"' target='&#95;blank'>"+NA.AdminPages.Encode(F.Name)+"</a></td>"+
			"<td class='number'>"+Round(F.Size/1024)+" KB</td>"+
			"<td>"+NA.AdminPages.Encode(F.LastWrite.ToString("yyyy-MM-dd HH:mm:ss"))+"</td></tr>"
	);

	Html+="</tbody></table>";
	]]((Html))[[
)
}}
