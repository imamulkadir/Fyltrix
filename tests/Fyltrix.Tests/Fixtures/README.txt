Test media generated with FFmpeg 9.0.2 (2 s, 160x90, test pattern + 440 Hz tone):

  ffmpeg -f lavfi -i testsrc2=size=160x90:rate=10 -f lavfi -i sine=frequency=440:sample_rate=22050 -t 2 -c:v libx264 -pix_fmt yuv420p -c:a aac -b:a 32k h264_aac.mkv
  ffmpeg -f lavfi -i testsrc2=size=160x90:rate=10 -f lavfi -i sine=frequency=440:sample_rate=22050 -t 2 -c:v mpeg4 -c:a libmp3lame -b:a 32k mpeg4_mp3.mkv

h264_aac.mkv plays in WebView2; mpeg4_mp3.mkv has a video codec Chromium can't decode (it falls back to Media Foundation).
