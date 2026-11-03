# Ripparr

Turns a Blu-ray or UHD Blu-ray backup (an ISO file or a BDMV folder, on scratch or on a USB drive) into one
finished MKV, named and tagged the way the library expects, ready for Radarr to import.

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

For foreign-language films there are two more steps on the same form:

- **Preview subtitles** reads the whole film once (minutes for a UHD disc) and pulls out every picture subtitle
  track. Each track gets its exact caption count, its forced captions and a preview page with every caption's
  picture. Tesseract also reads a sample of 120 captions per track: a track with SDH text (`[door slams]`,
  `JOHN:`, ♪) in a tenth of them is ticked SDH (when the profile detects SDH), and one where many captions talk
  about making the film ("this scene", "we shot", "on set", "the director", in English, French, German,
  Spanish or Italian) is ticked Commentary. A later track in the same language with a third more captions than
  the first backs that guess up. Hover the badge for what was found.
- **Read subtitles** then has Tesseract read every caption of every track: SDH and commentary are checked again
  on all of them (and set both ways), and the text shows in the preview and in **Side by side**, which lines up
  the captions of the tracks you tick by time (filter: rows where a track has nothing, to find the forced one).

Both run in the background, go on if the page is left, are kept in the subtitle folder (`disc-*`, pruned with
the job previews) and work on folders and decrypted ISOs.

## What a job does

1. **Check**: finds the playlist, the size it needs and whether the disc is encrypted (AACS folder, or the
   encryption bits in the first stream packets).
2. **Prepare**: reads a plain BDMV folder where it is, unpacks a plain ISO with 7-Zip, or makes a decrypted
   backup with `makemkvcon backup --decrypt` when the source is encrypted.
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

## Profiles

A profile decides the track plan: audio and subtitle languages in order (`original` is the film's own language
from Radarr), commentaries, lossy tracks next to lossless ones, the TrueHD AC-3 core, track name templates,
chapter names, the file name and MKV title templates, the global tags, the SRT (OCR) languages and the
DGDemux arguments (empty = the global default; the profile page shows the exact command and DGDemux's own
options). Two come built in (UHD remux and
Blu-ray remux), and they can be copied and changed. The plan can still be edited per job on the disc page.

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

## Building

```
dotnet test
dotnet run --project src/Ripparr     # uses dev/ folders from appsettings.Development.json
docker build -t ripparr .
```

## Licence

GPLv3, as the Muxarr layout and styles it reuses.
