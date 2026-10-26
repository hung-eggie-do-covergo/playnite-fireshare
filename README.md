# Fireshare Share

A Playnite extension that uploads the screenshots and clips you choose to your own
[Fireshare](https://github.com/ShaneIsrael/fireshare) instance and copies the share links.

## Install

Download the `.pext` from [Releases](https://github.com/hung-eggie-do-covergo/playnite-fireshare/releases) and open it with Playnite.

Requires Fireshare 1.9 or newer, which added upload tokens.

## Setup

1. In Fireshare, go to **Settings → Security → Upload Tokens** and create a token.
2. In Playnite, open **Add-ons → Extension settings → Fireshare Share**. Enter your Fireshare URL and the token, then click **Test connection**.
3. Fireshare makes new uploads private, and a private link asks the viewer to log in. To share links with anyone, set the default privacy for videos and images to public in Fireshare.

## Use

Right-click a game and choose **Share captures to Fireshare…**.

- The file picker opens in your captures folder, filtered to that game. It defaults to `Videos\Captures`, where Xbox Game Bar saves.
- Switch to **All captures** when Game Bar named the files after a different window title.
- Pick one or more files. The links are copied to the clipboard, one per line.
- Files already on Fireshare are not uploaded again. Their existing link is copied.
- Uploads are linked to the game when Fireshare already has a game with the same name.

Supported files: `png`, `jpg`, `jpeg`, `webp`, `gif`, `mp4`, `m4v`, `mov`, `webm`. HDR `.jxr` screenshots are not supported by Fireshare; share the `.png` Game Bar saves next to them.

The token is stored encrypted with Windows DPAPI for your user account.

## Build

```sh
./pack.sh                 # writes dist/FireshareShare-<version>.pext
dotnet run --project tests
```

Builds on macOS, Linux or Windows with the .NET SDK.
