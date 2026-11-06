# Security

Volna can read a browser's sign-ins and starts external programs, so problems here matter. Please report a vulnerability privately through [GitHub's security advisories](../../security/advisories/new) instead of opening a public issue.

Particularly welcome reports:

- A way for a link, a video's title or description, or a model using the agent to run code or read files outside the downloads folder
- Cookies or sign-in data reaching anywhere other than the site they belong to
- A tool download that gets around its checksum

## What Volna does with sign-in data

- It never sees a password. Sign-in happens in a browser.
- With a browser sign-in, it asks yt-dlp to read that browser's cookies for each request. yt-dlp holds all of that browser's cookies in memory for that run, and sends each only to the site it belongs to. Nothing is stored.
- With a separate YouTube window, only the youtube.com cookies are kept. An imported `cookies.txt` is kept whole, so import only what you mean to use. Both sit in the data folder, readable only by your account on macOS and Linux. On Windows, the file sits in your user profile with its default permissions.
- *Sign out here* deletes what Volna stored. Your browser stays signed in.

Do not share your data folder or a `cookies.txt` with anyone.

## What leaves your computer

Besides the sites you download from, Volna talks to GitHub for its tools and to see whether a new version exists, the FFmpeg builds' host, YouTube Music for searches, and, only with *Lyrics* on, LRCLIB, which receives the song's name and artist.

## What the agent can reach

A model using `volna-agent` can download into the downloads folder and open only the media files in it named the way Volna names them. It cannot reach other files, sign in, or read cookies. What it reads from videos is written by strangers and may try to instruct it, so be careful when giving the same model tools that can act on your behalf.
