# Ripparr

Turns a Blu-ray or UHD Blu-ray backup (an ISO file or a BDMV folder, on scratch or on a USB drive) into one
finished MKV, named and tagged the way the library expects, ready for Radarr (or Sonarr, for TV discs) to import.

MakeMKV decrypts only when it has to, DGDemux splits the playlist, MKVToolNix builds the MKV, and MediaInfo
and dovi_tool check the result. A sister app of [Salvarr](https://github.com/yannb2/salvarr) and
[Specarr](https://github.com/yannb2/Specarr), with the same look as the [Muxarr fork](https://github.com/yannb2/muxarr).

## Before the job: the quick read

Picking a playlist on the disc page reads its streams in a few seconds, before anything is queued: MediaInfo
on the main clip for codec, channels, sample rate, bit depth, picture size, HDR and Dolby Vision, and the
transport stream packets counted per track in samples spread over the clips for each track's bit rate and
size (a constant bit rate, as AC-3 or a DTS core, is taken as it is). For an ISO, 7-Zip reads the first
384 MB of the main clip, so the bit rates and sizes are estimates (≈). An encrypted disc cannot be read
before MakeMKV has decrypted it: the form then shows what the playlist says (resolution, bit depth, HDR10,
HDR10+ or Dolby Vision, frame rate, sample rate) and the full details come with the job.

For foreign-language films there are more steps on the same form:

- **Check SDH and commentary** is the quick one: it reads 32 slices of 32 MB spread over the film (seconds, not
  minutes) instead of every byte, and runs the same SDH and commentary checks on the captions found there. The
  caption counts are from that sample only. Without the mount service an ISO is read from its first 2 GB.
  **Preview subtitles (whole film)** is still there when the exact counts or every caption are wanted.
- **Preview subtitles** reads the whole film once (minutes for a UHD disc) and pulls out every picture subtitle
  track. Each track gets its exact caption count, its forced captions and a preview page with every caption's
  picture. Tesseract also reads a sample of 120 captions per track: a track with SDH text (`[door slams]`,
  `JOHN:`, ♪) in a tenth of them is ticked SDH (when the profile detects SDH), and one where many captions talk
  about making the film ("this scene", "we shot", "on set", "the director", in English, French, German,
  Spanish or Italian) is ticked Commentary. A later track in the same language with a third more captions than
  the first backs that guess up. Hover the badge for what was found.
- **Read subtitles** then has Tesseract read every caption of every track: SDH and commentary are checked again
  on all of them (and set both ways), and the text shows in the preview and in **Side by side**, which lines up
  the captions of the tracks you tick (the **compare** box next to each subtitle track's preview eye) by time (filter: rows where a track has nothing, to find the forced one).

For a French track, the OCR'd text can also say which French it is: words such as *fin de semaine*, *courriel*,
*stationnement* or *dépanneur* show **Looks like Québec French**, and *week-end*, *parking*, *portable* or
*bagnole* show **Looks like France French** (two words at least, and more of one side than the other). Clicking
the badge sets the language; the **France · Québec** links under any French track's language box do the same.
Québec French is written as `fr-CA` in the MKV and named *French (Canada)* in the track name; a disc only says
`fre`, whichever French it is.

All of them run in the background, go on if the page is left, are kept in the subtitle folder (`disc-*`, pruned with
the job previews) and work on folders and decrypted ISOs.

### Commentary or audio description?

A stereo lossy track after the multichannel one in the same language is either a commentary or an audio
description, and the disc does not say which. Such a track is kept (not as the default) and shows **Commentary
or AD?** with two buttons to answer. The **Listen** button (speaker icon) next to any audio track plays
20 seconds of it, from 40% into the film, in the browser: cut by ffmpeg to AAC and kept with the extras pictures.
It works on folders and on plain ISOs with the mount service, not on encrypted discs.

## What a job does

1. **Check**: finds the playlist, the size it needs and whether the disc is encrypted (AACS folder, or the
   encryption bits in the first stream packets).
2. **Prepare**: reads a plain BDMV folder where it is, has the mount service mount a plain ISO read-only
   (nothing copied; see below), or makes a decrypted backup with `makemkvcon backup --decrypt` when the source
   is encrypted. When the ISO can't be mounted the job stops and asks: **Unpack with 7-Zip** (the playlist's
   clips only, the size is in the question) or **Try mounting again**.
3. **Demux**: DGDemux writes each stream of the playlist, the Dolby Vision enhancement layer included.
4. **Read streams**: MediaInfo reads every stream (codec, channels, Atmos, DTS:X, HDR format).
5. **Subtitles**: every picture subtitle track (PGS) is kept in `/config/subtitles/job-<id>/` for the preview
   page (each caption's picture and time). For the profile's SRT languages, Tesseract reads the pictures, the
   text for the hard of hearing is removed ([door slams], (laughs), `JOHN:`, ♪ lyrics) and an SRT track goes in
   right after the picture track. An OCR problem is a warning, not a failure.
6. **Dolby Vision**: a separate enhancement layer is merged back into the video with `dovi_tool mux`
   (profile 7 FEL stays FEL), always. Only the job form can leave it out, for a disc whose layer will not demux.
7. **Build the MKV**: mkvmerge with the track plan (languages, names, default/forced/commentary flags, order),
   chapters from the playlist, the frame rate from the playlist and the global tags.
8. **Verify**: tracks, languages, duration and chapters against the plan; HDR format; for Dolby Vision, the RPU
   frame count against the video.
9. **Finish**: moves the file to `done/Title (Year)/`.

Each step logs the exact command it ran, on the job page and in `/config/logs/job-<id>.log`.

The job page shows the current step's percentage and the time left in it (from its pace so far), and the whole
job's percentage: the steps done plus the current one's share, each step weighted by how long it usually takes
(copying or decrypting the disc, DGDemux and mkvmerge move all the data; the OCR counts per SRT track). The
queue shows both too, and `/api/stats` gives the whole-job figure.

## Mount ISOs instead of unpacking

A container can't mount an ISO without being privileged, so a small service on the host does it:
[`host/ripparr_mount.py`](host/ripparr_mount.py) (Python, standard library only). Ripparr asks it over a unix
socket; it mounts the ISO with `mount -o loop,ro,nosuid,nodev,noexec` (UDF, as Blu-rays are) under its folder,
which the container sees through an `rslave` volume, and unmounts it when the job ends. It only mounts `.iso`
files from the folders it is told to allow, and only unmounts its own mounts.

```sh
sudo install -m 755 host/ripparr_mount.py /usr/local/sbin/ripparr_mount.py
sudo install -m 644 host/ripparr-mount.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now ripparr-mount
```

The unit mounts in `/mnt/pc/iso-mounts`, allows ISOs from `/downloads` and `/mnt/pc`, and lets gid 1000 use
the socket; change `--root`, `--allow` and `--group` in it to suit. The service works with host paths: a volume that
changes the path (`/mnt/usb:/usb`) needs `Ripparr__IsoMount__PathMap__0__Container: /usb` and
`__Host: /mnt/usb` (nothing to set when the volumes keep the path, like `/downloads:/downloads`). Then give
the container the socket folder (`/run/ripparr-mount:/run/ripparr-mount`) and make sure it sees the mount
folder (already the case under `/mnt/pc` with `rslave`); set `Ripparr__IsoMount__Folder` when it is elsewhere.
The disc page says whether the service answers. By hand: `sudo ripparr_mount.py mount /downloads/film.iso`,
`list`, `unmount <path>`.

## Extras

Under the film's form, the disc page has an **Extras** card listing the disc's other playlists (not the film's,
nothing under 10 seconds, no duplicates). Tick the ones to keep, give each a type and a name, and queue them:
one job each, with the title, year, IDs and tags of the film's form. **Pictures** shows three frames of a
playlist (at a tenth, half and nine tenths) to tell them apart; they are cached in `/config/thumbnails`. For an
ISO they are streamed out with 7-Zip, so only from clips up to 8 GB.

A playlist that is just other playlists one after the other is flagged **Play all** and hidden with the
duplicates (the switch shows them): ripping it would repeat the others.

The extras use the **Extras** profile (original language and English audio, English and French subtitles, no
OCR) unless another is picked. An extra is named after what was typed (`Making Of.mkv`, MKV title
`Making Of`; empty gives `Featurette 2`) and goes to `done/Title (Year)/<type>/`. The types are the folder
names that both Jellyfin and Radarr leave alone: `featurettes`, `behind the scenes`, `deleted scenes`,
`interviews`, `scenes`, `shorts`, `trailers` and `extras`. Move the type folder next to the film in the library.

## TV discs

When most playlists have about the same length (three or more, within a quarter, nothing much longer
beside them), the disc page switches to **TV episodes**; the switch at the top changes it either way. Look the
series up in **Sonarr**: the title, year, TVDB, TMDb and IMDb IDs, the original language and, for Sonarr's
Anime series type, absolute numbers. Set the season and the first episode on the disc: the ticked playlists are
numbered from it in disc order (any number can be typed over), and get their titles from Sonarr (or typed ones).
**Pictures** show three frames of each playlist to check the order. Ripparr remembers where the last disc of a
series stopped, so the next disc starts at the next episode.

Each episode is its own job and goes to `done/Series (Year)/Season 01/` (season 0 to `Specials`) as
`Series S01E05 [1080p][AVC][SDR][TrueHD-5.1][BDRemux] - Episode Title.mkv`, with ` (005)` after `S01E05` for
anime, MKV title `Series S01E05 - Episode Title`, ready for a Sonarr manual import. The profile page has the
episode templates (`{episode}`, `{absolute}`, `{episodetitle}`). The tags add `TVDB2` (`series/<id>`),
`TMDB` as `tv/<id>` and `ABSOLUTE_EPISODE`.

Episodes cut out of one big clip (common on TV discs) are told apart by their in and out times, and the "play
all" playlist that plays the whole clip is flagged. The **Extras** card works on TV discs too: the extras go to
the series folder, or to the season's folder (`Season 01/extras/`). Sonarr skips the same folder names.

## Queue and History

The queue shows what is waiting, running, or needs a look (failed, interrupted, waiting for an answer). Every
row has **Remove**: a queued or running job is cancelled, the others move to **History**, where done, failed
and cancelled jobs stay with their result, a search, a status filter, Retry, and a delete that forgets a job and
its log (never the MKV).

## Profiles

A profile decides the track plan: audio and subtitle languages in order (`original` is the film's own language
from Radarr), commentaries, lossy tracks next to lossless ones, the TrueHD AC-3 core, track name templates,
chapter names, the file name and MKV title templates, the global tags, the SRT (OCR) languages and the
DGDemux arguments (empty = the global default; the profile page shows the exact command and DGDemux's own
options). Three come built in (UHD remux,
Blu-ray remux and Extras), and they can be copied and changed. The plan can still be edited per job on the disc page.
There, the **Description** box of a track fills `{description}` in the profile's name template (the commentary
template `{language} - {codec} {channels} ({description})` gives `English - AC3 2.0 (Director and Writer)`); a
template without `{description}` gets the text in brackets at the end, and `=Name` types the whole name.

The default file name follows Radarr's Standard Movie Format in this library:
`Title (YYYY) - Edition [2160p][HEVC][DV HDR10][TrueHD Atmos-7.1][BDRemux].mkv`.

## Tools and updates

Every tool is built into the image at a pinned version (the `ARG` lines at the top of the `Dockerfile`).
The Tools page shows what each one reports, the pin and the newest upstream version (checked once a day).
The weekly **Tool updates** workflow opens a pull request that bumps the pins; merging it builds a new image.

**MakeMKV is held at 1.x.** 2.0 (September 2026) came with reports of discs that no longer open and keys that
are rejected, so neither the workflow nor the Dockerfile moves to 2.x unless asked
(`allow_makemkv_major` on the workflow, `Ripparr__Updates__AllowMakeMkvMajor=true` for the Tools page).

**DGDemux**: rationalqm.us refuses downloads from build servers, so the image may come without it (the build
log and the Tools page say so). Download `dgdemux_linux_<version>.zip (1.0.0.85 is the pinned one)` yourself and put the `dgdemux` binary in
`/config/tools/dgdemux` (executable), with `DGDemux.txt` from the same zip next to it (`dgdemux -h` prints that
file); Ripparr uses a binary there instead of the image's.

The MakeMKV key is entered on the Tools page and saved in `/config/makemkv/.MakeMKV/settings.conf`. It never
belongs in compose, `.env` or git. The image includes MakeMKV, whose licence you accept by building it, so keep
the image for your own use.

## Running it

See [compose.example.yaml](compose.example.yaml). Port 8186. `/health` answers `ok`, and `/api/stats` gives
the queue, the current job and its progress for a Homepage tile.

| Setting (environment) | Default |
|---|---|
| `Ripparr__Sources__0__Name` / `__Path` | `Scratch` = `/downloads`, `USB drives` = `/usb` |
| `Ripparr__WorkFolder` | `/downloads/ripparr/work` |
| `Ripparr__DoneFolder` | `/downloads/ripparr/done` |
| `Ripparr__MaxParallelJobs` | `1` |
| `Ripparr__CleanWorkFolder` | `true` (a failed job always keeps its folder) |
| `Ripparr__SpareSpaceGb` | `20` |
| `Ripparr__DgDemuxArguments` | `-i {input} -o {output}` (a profile can have its own) |
| `Ripparr__SubtitleFolder` | `/config/subtitles` |
| `Ripparr__KeepSubtitlePreviews` | `30` (jobs whose subtitle pictures are kept) |
| `Ripparr__Radarr__Url` / `__ApiKey` | not set: title, year and IDs are typed by hand |
| `Ripparr__Sonarr__Url` / `__ApiKey` | not set: series, IDs and episode titles are typed by hand |
| `Ripparr__IsoMount__Enabled` | `true` (`false`: always unpack ISOs with 7-Zip) |
| `Ripparr__IsoMount__Socket` | `/run/ripparr-mount/mount.sock` |
| `Ripparr__IsoMount__Folder` | `/mnt/pc/iso-mounts` (where the service mounts; left out of the sources) |
| `Ripparr__IsoMount__WhenItFails` | `Ask` (`Unpack`: unpack with 7-Zip without asking) |
| `Ripparr__IsoMount__PathMap__0__Container` / `__Host` | none (folders whose path differs on the host) |

## Building

```
dotnet test
dotnet run --project src/Ripparr     # uses dev/ folders from appsettings.Development.json
docker build -t ripparr .
```

## Licence

GPLv3, as the Muxarr layout and styles it reuses.
