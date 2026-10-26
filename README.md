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

In the game menu, choose **Share captures to Fireshare…**. In fullscreen mode it is under **Extensions**.

- A grid shows the game's newest captures from your captures folder. It defaults to `Videos\Captures`, where Xbox Game Bar saves.
- Controller: D-pad moves, **A** selects, **X** shares, **Y** switches between this game and all captures, **B** goes back. With nothing selected, **X** shares the focused capture.
- Mouse and keyboard work too.
- The links are copied to the clipboard, one per line. Fullscreen mode also shows them in a dialog.
- Files already on Fireshare are not uploaded again. Their existing link is copied.
- Uploads are linked to the game when Fireshare already has a game with the same name.

Supported files: `png`, `jpg`, `jpeg`, `webp`, `gif`, `mp4`, `m4v`, `mov`, `webm`. HDR `.jxr` screenshots are not supported by Fireshare; share the `.png` Game Bar saves next to them.

The token is stored encrypted with Windows DPAPI for your user account.

## Theme integration

Themes can offer a share button wherever they show a screenshot. The command takes a file path, or an item with a `FileName` property such as a ScreenshotsVisualizer screenshot:

```xml
<!-- {PluginSettings} works only on a normal element inside a template -->
<ContentControl x:Name="Fireshare_Command" Content="{PluginSettings Plugin=FireshareShare, Path=ShareCommand}"
                Visibility="Collapsed" Focusable="False" />

<ListView.InputBindings>
    <pin:GameControllerInputBinding Button="X" Command="{Binding Content, ElementName=Fireshare_Command}"
                                    CommandParameter="{Binding SelectedItem, ElementName=ListSV}" />
</ListView.InputBindings>
```

## Build

```sh
./pack.sh                 # writes dist/FireshareShare-<version>.pext
dotnet run --project tests
```

Builds on macOS, Linux or Windows with the .NET SDK.
