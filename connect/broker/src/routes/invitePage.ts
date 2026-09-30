// GET /i: the landing page behind invite links (https://onesalem-connect-broker-production.onesalemconnect.workers.dev/i#<secret>). The secret
// is in the URL fragment, which browsers never send, so this Worker never sees it and the page is
// the same static document for everyone. Its one script copies the page's own address (fragment
// included) to the clipboard for pasting into 1Salem Connect; nothing is loaded from elsewhere.

import type { RequestContext } from "../context";
import { utf8 } from "../encoding";
import { sha256 } from "../crypto";

const SCRIPT =
  'document.getElementById("copy").addEventListener("click",function(){' +
  'navigator.clipboard.writeText(location.href).then(function(){' +
  'document.getElementById("copied").hidden=false;});});' +
  'if(!location.hash){document.getElementById("copy").disabled=true;}';

/** Only an absolute https URL is shown as the download link; anything else is left out. */
function downloadUrl(value: string | undefined): string | null {
  if (value === undefined) return null;
  try {
    const url = new URL(value);
    return url.protocol === "https:" && url.username === "" && url.password === "" ? url.href : null;
  } catch {
    return null;
  }
}

const escape = (text: string) =>
  text.replace(/&/g, "&amp;").replace(/"/g, "&quot;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

export async function invitePage(ctx: RequestContext): Promise<Response> {
  const download = downloadUrl(ctx.env.CONNECT_DOWNLOAD_URL);
  // The first thing a friend needs is the app, so the download is a button above the steps.
  const button = (label: string, note: string) =>
    download === null
      ? ""
      : `<p><a class="download" href="${escape(download)}">${label}</a></p>\n<p class="note">${note}</p>`;
  const html = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="referrer" content="no-referrer">
<title>1Salem Connect invitation</title>
<style>
body{margin:0;background:#08131f;color:#f1f6fa;font:16px/1.5 "Segoe UI",system-ui,sans-serif}
main{max-width:640px;margin:0 auto;padding:32px 20px}
section{background:#0e1c2b;border:1px solid #3b566d;border-radius:12px;padding:20px 24px;margin:0 0 20px}
h1,h2{margin:0 0 8px}h1{font-size:24px}h2{font-size:18px}p,ol{color:#c9d6e2}a{color:#72e2de}
button{background:#2cb8b3;color:#031719;border:0;border-radius:8px;padding:10px 18px;font:600 15px "Segoe UI",system-ui,sans-serif;cursor:pointer}
button:disabled{background:#405064;color:#9fb0c2;cursor:default}
a.download{display:inline-block;background:#2cb8b3;color:#031719;border-radius:8px;padding:12px 22px;font:600 17px "Segoe UI",system-ui,sans-serif;text-decoration:none}
.note{font-size:14px;margin-top:0}
</style>
</head>
<body>
<main>
<section>
<h1>You're invited to a game server</h1>
<p>Someone who runs a 1Salem Server Manager server sent you this invitation. Joining uses the free 1Salem Connect app; only the game connection goes through it.</p>
${button("Download 1Salem Connect", "For Windows 10 and 11. Run the downloaded setup to install it.")}
<ol>
<li>Install and open 1Salem Connect.</li>
<li>Choose <strong>Add a server</strong> and paste this page's address.</li>
<li>Wait for the server owner to approve your PC.</li>
</ol>
<p><button id="copy" type="button">Copy invitation link</button> <span id="copied" hidden>Copied.</span></p>
<p>The invitation works once. Don't share it with anyone else.</p>
</section>
<section lang="ar" dir="rtl">
<h2>تمت دعوتك إلى خادم ألعاب</h2>
<p>أرسل إليك شخص يدير خادمًا عبر 1Salem Server Manager هذه الدعوة. يتم الانضمام باستخدام تطبيق 1Salem Connect المجاني، ويمر اتصال اللعبة وحده عبره.</p>
${button("تنزيل 1Salem Connect", "لنظامي Windows 10 و11. شغّل ملف الإعداد الذي نزّلته لتثبيته.")}
<ol>
<li>ثبّت 1Salem Connect وافتحه.</li>
<li>اختر <strong>إضافة خادم</strong> والصق عنوان هذه الصفحة. (زر النسخ أعلاه ينسخ الدعوة.)</li>
<li>انتظر موافقة صاحب الخادم على جهازك.</li>
</ol>
<p>تعمل الدعوة مرة واحدة فقط. لا تشاركها مع أي شخص آخر.</p>
</section>
</main>
<script>${SCRIPT}</script>
</body>
</html>
`;
  // CSP hash sources are standard (not URL-safe) base64.
  const scriptHash = btoa(String.fromCharCode(...(await sha256(utf8(SCRIPT)))));
  return new Response(html, {
    status: 200,
    headers: {
      "Content-Type": "text/html; charset=utf-8",
      "Content-Security-Policy":
        `default-src 'none'; script-src 'sha256-${scriptHash}'; style-src 'unsafe-inline'; ` +
        "base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
      "Referrer-Policy": "no-referrer",
      "X-Content-Type-Options": "nosniff",
      "Cache-Control": "public, max-age=300",
    },
  });
}
