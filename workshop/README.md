# Steam Workshop workspace

This folder is the workspace for Mega Crit's [sts2-mod-uploader](https://github.com/megacrit/sts2-mod-uploader) (`ModUploader.exe`).

| File | Status |
|---|---|
| `workshop.json` | Title, BBCode description, change note, tag `QoL`. `visibility` starts as `private`. |
| `content/` | Not tracked. `./build.ps1 -Workshop` fills it with the release files (DLL, `SpireRewind.json`, LICENSE, README). |
| `image.png` | Cover image, required by the uploader. Must be under 1 MB; `./build.ps1 -Workshop` fails while it is missing or too large. Do not use Mega Crit's logo or official art. |
| `previews/` | Optional. Each image under 1 MB (checked by `build.ps1 -Workshop`). If the folder is absent, existing previews on Steam stay unchanged. |
| `mod_id.txt` | Written by the first upload. Commit it, so later uploads update the same Workshop item. |

## Upload

```powershell
./build.ps1 -Workshop
ModUploader.exe upload -w path\to\spire-rewind\workshop
```

Before each upload, update `changeNote` to match the CHANGELOG entry and check that the version in `content/SpireRewind.json` is the one you mean to publish. `content/` must hold exactly one `.json` (the manifest), because the game reads every `.json` under a mod folder as a manifest. Set the supported game branches on the Workshop web page rather than in `workshop.json`; the uploader's README notes that `minBranch` / `maxBranch` behave inconsistently.

## First release: private preview, then public

1. Upload with `visibility` left at `private`. Commit the `mod_id.txt` the uploader writes.
2. On the Workshop item page (visible to the uploading account only), check the title, description, thumbnail and tags, then subscribe from the same account and start the game: the mod must appear in the game's mod list and load.
3. Only then switch the item to public: either set `"visibility": "public"` in `workshop.json` and upload again, or change the visibility on the item page. The description's Steam Cloud warning stays until the Steam Cloud test in `docs/TESTING.md` has been done.
