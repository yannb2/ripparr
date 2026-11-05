# Ripparr

Turns a Blu-ray or UHD Blu-ray backup (an ISO file or a BDMV folder, on scratch or on a USB drive) into one
finished MKV, named and tagged the way the library expects, ready for Radarr (or Sonarr, for TV discs) to import.

MakeMKV decrypts only when it has to, DGDemux splits the playlist, MKVToolNix builds the MKV, and MediaInfo
and dovi_tool check the result. A sister app of [Salvarr](https://github.com/yannb2/salvarr) and
[Specarr](https://github.com/yannb2/Specarr), with the same look as the [Muxarr fork](https://github.com/yannb2/muxarr).

The **Sources** page lists every ISO and BDMV folder it finds, newest first. Click any column header to sort by it,
and click it again to flip the order. Size and Changed start biggest and newest first, the others A to Z. The sort
is kept in the address (`?sort=size&dir=asc`), so a reload or a bookmark keeps it.

## Finding the film on discs with fake playlists

Ripparr takes the longest playlist as the film, then the one with most chapters. Some discs hide the
film among fakes, and the disc page then says so and offers two checks, the ones people use by hand
([MakeMKV forum: deobfuscating playlists](https://forum.makemkv.com/forum/viewtopic.php?p=53480),
[GetMPLS](https://forum.makemkv.com/forum/viewtopic.php?p=86401)):

- **Too little data.** A long playlist under 3 Mbit/s loops a few tiny clips (The Fabelmans UHD has a
  4-hour one of 540 MB). It is never taken as the film and gets a red badge.
- **Lookalikes.** Playlists near the film's length made of its clips in other orders are marked; the
  "own clips" badge counts clips no other long playlist uses, as GetMPLS does.
- **Check clip joins.** The film's clips follow each other seamlessly, so the last picture of one clip
  and the first of the next look alike; a fake's shuffled order jumps. Ripparr reads those two pictures
  of every clip with ffmpeg and scores each lookalike by the mean change at its joins (0 = seamless).
  A clear winner (under 70% of the next score) becomes the main feature.
- **Ask MakeMKV.** makemkvcon plays the disc's menus and names one title the main feature
  (FPL_MainFeature); Ripparr takes it. Its answer comes before the joins check. The menus of a
  fake-playlist disc are BD-J (Java) programs, so the image has a Java runtime (`default-jre-headless`,
  on the Tools page); MakeMKV also needs it for some BD+ and AACS Soft-KCD discs
  ([makemkv.com/bdjava](https://makemkv.com/bdjava)). Without it the answer says so.
- **No playlist for the film.** Some discs have no playlist for the film at all: their BD-J menu builds
  it at play time. The Fabelmans UHD has 4 short playlists, and the film is one 82 GB clip
  (`00294.m2ts`) that none of them plays. Every clip over 1 GB that no playlist plays gets a playlist
  made from its `CLIPINF` file (length, every stream and its language), listed as **Clip 00294** with
  a "No playlist" badge. The job writes that playlist into its work folder, next to links to the
  disc's `STREAM` and `CLIPINF` folders, and rips it like any other. It has no chapters: they are in
  the playlist the menu builds. When the playlist taken as the film holds less than a quarter of a big
  disc, the page says so and offers both checks.
- **Faked length.** A playlist that claims a few seconds but holds gigabytes (over 200 Mbit/s, more
  than any Blu-ray plays) has faked times: it is not hidden as short, and it beats every length.

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

SDH and commentary flags you set by hand stay as you set them: a later read only changes the ones it set itself.

Everything typed and ticked on the disc page (title, year, IDs, each playlist's tracks, profile and tags, the
extras card) is kept while the app runs: a subtitle preview, the job page after a dry run or another menu does
not reset the form. **Start over** puts the page back as on a first visit; **Read again** reads the disc anew
and plans the tracks anew.

### Commentary or audio description?

A stereo lossy track after the multichannel one in the same language is either a commentary or an audio
description, and the disc does not say which. Such a track is kept (not as the default) and shows **Commentary
or AD?** with two buttons to answer. The **Listen** button (speaker icon) next to any audio track plays
20 seconds of it, from 40% into the film, in the browser: cut by ffmpeg to AAC and kept with the extras pictures.
It works on folders and on plain ISOs with the mount service, not on encrypted discs.

## What a job does

On the job form, each track has Keep, language, Default, flag buttons (Forced, SDH, Commentary, AD, with the
icons Muxarr uses) and a description; drag a track by its grip (or use the arrows) to change the order within
its kind. The job page shows the same flags as icons, and folds its Checks card when every check passed.

1. **Check**: finds the playlist, the size it needs and whether the disc is encrypted (AACS folder, or the
   encryption bits in the first stream packets).
2. **Prepare**: reads a plain BDMV folder where it is, has the mount service mount a plain ISO read-only
   (nothing copied; see below), or makes a decrypted backup with `makemkvcon backup --decrypt` when the source
   is encrypted. When the ISO can't be mounted the job stops and asks: **Unpack with 7-Zip** (the playlist's
   clips only, the size is in the question) or **Try mounting again**.
3. **Demux**: DGDemux writes each stream of the playlist, the Dolby Vision enhancement layer included. Some
   UHD discs keep the layer in a clip of its own (a sub path of the playlist), which DGDemux leaves out: ffmpeg
   then copies it out of that clip. Before the demux, the job checks that the layer's clip really has its
   stream. Some playlists give the layer a PID the clip does not use (0x1500 where the clip has 0x1015): the
   clip's one other HEVC stream is then taken as the layer. With no such stream the job stops at once
   (instead of after the whole demux).
4. **Read streams**: MediaInfo reads every stream (codec, channels, Atmos, DTS:X, HDR format).
5. **Subtitles**: every picture subtitle track (PGS) is kept in `/config/subtitles/job-<id>/` for the preview
   page (each caption's picture and time). For the profile's SRT languages, Tesseract reads the pictures, the
   text for the hard of hearing is removed ([door slams], (laughs), `JOHN:`, ♪ lyrics) and an SRT track goes in
   next to the picture track. An OCR problem is a warning, not a failure. Then the subtitle tracks are sorted:
   by the profile's languages, then within each language by kind in the profile's **Order within a language**
   (Forced, Full, SDH, Commentary by default), then SRT or PGS first (a profile switch), then as on the disc.
   An SRT made without the SDH text counts as Full, so it comes before the SDH picture track.
6. **Dolby Vision**: a separate enhancement layer is merged back into the video with `dovi_tool mux`
   (profile 7 FEL stays FEL), always. Only the job form can leave it out, for a disc whose layer will not demux.
7. **Build the MKV**: mkvmerge with the track plan (languages, names, default/forced/commentary flags, order),
   chapters from the playlist (named as the profile says; the Chapters card on the job form renames any of them,
   written as UTF-8), the frame rate from the playlist and the global tags. The **Release label** box
   on the job form (Criterion, Arrow, Kino Lorber...) is who put out the disc, not the cut (that is the
   edition); it is written as the standard Matroska tag `DISTRIBUTED_BY`, which tag_release keeps on later
   retags and edition_tags.py shows in Jellyfin as a `Release: Criterion` tag.
   The **Source group** box is who released the ISO (FraMeSToR...): it is read from the ISO's name (the part
   after the last dash of a release name) and written as `SOURCE_RELEASE_GROUP`, kept apart from your own
   `RELEASE_GROUP`. It stays empty for a disc label or a name of your own; clear it for your own rips.
8. **Verify**: tracks (count and order), languages, duration and chapters against the plan (track names, flags and the MKV title only warn); HDR format; for Dolby Vision, the RPU
   frame count against the video.
9. **Finish**: moves the file to `done/Title (Year)/`.

Each step logs the exact command it ran, on the job page and in `/config/logs/job-<id>.log`. When a job finishes, a copy
of its log is saved next to the MKV with the same name (`Film (2020) [...].log`).

The job page and the queue show two bars: the current step, with the time left in it (from its pace so far),
and the whole job: the steps done plus the current one's share, each step weighted by how long it usually takes
(copying or decrypting the disc, DGDemux and mkvmerge move all the data; the OCR counts per SRT track).
`/api/stats` gives the whole-job figure. DGDemux prints no percentage when its output is not a terminal, so its
progress (and that of `dovi_tool mux` and the ffmpeg copy of a Dolby Vision layer) is the bytes the tool has read
so far (`/proc/<pid>/io`) out of the size of the clips it reads.

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
Disc order is the order of the disc's "play all" playlist when it has one, else the order of the episodes' clips
(playlist numbers are often out of order); a line above the list says which was used. When the disc's own title
names a season ("Doctor Who - Season 1 - Disc 1"), the series and season are filled in from it.
**Pictures** show three frames of each playlist to check the order, and **Watch** plays 30 seconds from its
start, middle or end (made with ffmpeg the first time, 640 px wide; a Watch button is on every row of the
Playlists list too). Ripparr remembers where the last disc of a series stopped, so the next disc starts at the
next episode.

Some discs hide their episodes among fakes: Doctor Who's list a dozen "four hour" playlists that loop tiny clips
(left out as **Too little data**, also from the extras), and every episode twice, once with a second-long clip
after it (counted as a duplicate of the plain one). Their menus pick playlists from a register at play time, so
Ripparr cannot read the order from them; check it with Watch.

Each episode is its own job and goes to `done/Series (Year)/Season 01/` (season 0 to `Specials`) as
`Series S01E05 [1080p][AVC][TrueHD-5.1][BDRemux] - Episode Title.mkv`, with ` (005)` after `S01E05` for
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
its log (never the MKV in the done folder).

Removing a failed, cancelled or finished job (from the queue or from History) also deletes its work folder
(`job-<id>` under the work folder: demuxed streams, and the MKV of a job that failed its checks, after asking).
A retried job starts its demux from an empty folder.

## Logs

The **Logs** page shows the app's last 2000 log lines (newest first, kept in memory since the last start), with
a level filter, a search and the exception of an error one click away. `docker logs` has everything; each job's
own log is on its page.

## Settings

The sidebar's Settings page has four tabs:

- **Profiles**: the profiles below (the old Profiles page; `/profiles` still opens it).
- **Integrations**: Radarr and Sonarr, each with a Test button (`/api/v3/system/status`). What is saved here
  is kept in the database and wins over the `Ripparr__Radarr__…` / `Ripparr__Sonarr__…` environment variables,
  which only give the starting values; "Use environment variables" forgets the saved ones. No restart needed.
- **Notifications**: when a job ends, a message to an [ntfy](https://ntfy.sh) topic (done and failed, each with
  a switch; a good dry run only when asked) and, after a good job, a Jellyfin library scan
  (`POST /Library/Refresh`). Test buttons for both. A failure here is logged and never fails the job.
- **Status**: folders with free space and the values from the environment (the old Status page).

## Profiles

A profile decides the track plan: audio and subtitle languages in order (`original` is the film's own language
from Radarr), commentaries, lossy tracks next to lossless ones, the TrueHD AC-3 core, track name templates,
chapter names, the file name and MKV title templates, the global tags, the SRT (OCR) languages and the
DGDemux arguments (empty = the global default; the profile page shows the exact command and DGDemux's own
options). Three come built in (UHD remux,
Blu-ray remux and Extras), and they can be copied and changed. The plan can still be edited per job on the disc page.
There, the **Description** box of a track fills `{description}` in the profile's name template (the commentary
template `{language} - {codec} {channels} ({description})` gives `English - AC3 2.0 (Director and Writer)`); a
template without `{description}` gets the text in brackets at the end, and `=Name` types the whole name. The
subtitle commentary template `{language} (Commentary) - {description} - {codec}` gives `English (Commentary) - PGS`
with nothing typed and `English (Commentary) - Director - PGS` with "Director". (`{trackname}` is gone: saved
profiles that used it now use `{description}`.)

**Codec names** (under each track name, as in the Muxarr fork) set what `{codec}` writes for each detected codec:
`Dolby TrueHD` for TrueHD gives `English - Dolby TrueHD Atmos 7.1` (a blank Atmos label uses its codec's name with
"Atmos" added). The audio labels are the Muxarr fork's, in its order (AC-3, E-AC-3, DTS-HD Master Audio, DTS:X...),
so a list can be copied across, and a blank one writes that label, as Muxarr does (names saved under the older
labels such as AC3 move over on load). The video list also renames HDR10, HDR10+, HLG, SDR, FEL and MEL in
`{range}`, `{hdr}` and `{dvlayer}`. Only track names use them: the file name keeps Radarr's words. Each track
name's preview sits right under its template.

**Add a track without SDH when a language only has SDH subtitles** (Text subtitles card, on by default): when
every full subtitle track of a kept language is SDH, an SRT of it with the SDH text removed is added (placed by the order within a language),
named as a plain track (`English - SRT`). It works for languages outside the SRT list too: a track that was not
marked SDH and not read is checked on a sample of 120 captions first, and read in full only when it is SDH.

The default file name follows Radarr's Standard Movie Format in this library:
`Title (YYYY) - Edition [2160p][HEVC][DV HDR10][TrueHD Atmos-7.1][BDRemux].mkv`.
"SDR" is only written for 2160p video (`[SDR]` in the file name, `{range}` in the video track name): below 4K
every disc is SDR, so the HDR bracket is left out, the same rule as tag_release and Radarr's SDR format.
When the job has a RELEASE_GROUP tag, `{-releasegroup}` adds it at the end (`...[BDRemux]-YANNRIP.mkv`, spaces
become dots). That is where Radarr and Sonarr read the release group on import, so a custom format can match
your own rips. Profiles saved before 0.4.12 get `{-releasegroup}` added once, and removing it keeps it out.

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
| `Ripparr__Radarr__Url` / `__ApiKey` | not set: title, year and IDs are typed by hand (Settings > Integrations can set them) |
| `Ripparr__Sonarr__Url` / `__ApiKey` | not set: series, IDs and episode titles are typed by hand |
| `Ripparr__Notifications__NtfyUrl` / `__NtfyToken` | not set: no messages (Settings > Notifications can set them) |
| `Ripparr__Notifications__JellyfinUrl` / `__JellyfinApiKey` / `__RefreshJellyfin` | not set / `false` |
| `Ripparr__IsoMount__Enabled` | `true` (`false`: always unpack ISOs with 7-Zip) |
| `Ripparr__IsoMount__Socket` | `/run/ripparr-mount/mount.sock` |
| `Ripparr__IsoMount__Folder` | `/mnt/pc/iso-mounts` (where the service mounts; left out of the sources) |
| `Ripparr__IsoMount__WhenItFails` | `Ask` (`Unpack`: unpack with 7-Zip without asking) |
| `Ripparr__IsoMount__PathMap__0__Container` / `__Host` | none (folders whose path differs on the host) |

## Building

The subtitle code (PGS reader, OCR, SDH cleaner, classifier) is shared with the Muxarr fork in
[arrkit](https://github.com/yannb2/arrkit), a git submodule in `arrkit/`. Clone with
`--recurse-submodules`, or run `git submodule update --init` in an existing clone. To take a newer
arrkit: `git -C arrkit pull origin main`, then commit the `arrkit` change.

```
git clone --recurse-submodules https://github.com/yannb2/ripparr
dotnet test
dotnet run --project src/Ripparr     # uses dev/ folders from appsettings.Development.json
docker build -t ripparr .
```

## Licence

GPLv3, as the Muxarr layout and styles it reuses.
