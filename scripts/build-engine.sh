#!/usr/bin/env bash
set -euo pipefail
root="$(pwd)"
out="$root/.tools/engine"
mkdir -p "$out/licenses" "$out/fixtures" "$root/.tools/engine-build"
archive="ffmpeg-8.1.3.tar.xz"
curl --fail --location --retry 3 "https://ffmpeg.org/releases/$archive" -o "$out/licenses/$archive"
echo "7138d28c96d9d3e3af4ee3d8cad72741f8ffb40da90c1112235dea3ecd3178a3  $out/licenses/$archive" | sha256sum --check
tar -xf "$out/licenses/$archive" -C "$root/.tools/engine-build"
cd "$root/.tools/engine-build/ffmpeg-8.1.3"
flags=(
  --target-os=mingw32 --arch=x86_64 --enable-cross-compile
  --cross-prefix=x86_64-w64-mingw32- --cc=x86_64-w64-mingw32-gcc-posix
  --disable-everything --disable-autodetect --disable-network
  --disable-doc --disable-debug --disable-ffplay --disable-ffprobe
  --disable-shared --enable-static --disable-x86asm --disable-pthreads --enable-w32threads
  --enable-indev=dshow,lavfi --enable-protocol=file,pipe
  --enable-demuxer=rawvideo,h264,hevc,mjpeg,nut
  --enable-parser=h264,hevc,mjpeg
  --enable-muxer=framecrc,image2pipe,nut,null
  --enable-decoder=rawvideo,mjpeg,h264,hevc --enable-encoder=mjpeg,rawvideo
  --enable-filter=scale,format,null,anull,testsrc2 --enable-avfilter --enable-swscale
  --extra-ldflags=-static
)
printf '%q ' ./configure "${flags[@]}" > "$out/licenses/build-configure.txt"
printf '\n' >> "$out/licenses/build-configure.txt"
./configure "${flags[@]}"
make -j2 ffmpeg.exe
x86_64-w64-mingw32-strip ffmpeg.exe
cp ffmpeg.exe "$out/ffmpeg.exe"
cp COPYING.LGPLv2.1 "$out/licenses/FFmpeg-COPYING.LGPLv2.1"
cp LICENSE.md "$out/licenses/FFmpeg-LICENSE.md"
cp "$root/scripts/build-engine.sh" "$out/licenses/build-engine.sh"
x86_64-w64-mingw32-objdump -p ffmpeg.exe > "$out/licenses/PE-imports.txt"
if grep -Ei 'DLL Name:.*(libgcc|libwinpthread|libstdc\+\+|msys-)' "$out/licenses/PE-imports.txt"; then
  echo 'Unexpected non-system DLL dependency' >&2; exit 1
fi
x86_64-w64-mingw32-gcc-posix --version > "$out/licenses/toolchain.txt"
dpkg-query -W gcc-mingw-w64-x86-64-posix mingw-w64-common >> "$out/licenses/toolchain.txt"
cp /usr/share/doc/mingw-w64-common/copyright "$out/licenses/mingw-copyright.txt"
cp /usr/share/doc/gcc-mingw-w64-x86-64-posix/copyright "$out/licenses/gcc-copyright.txt"
sha256sum "$out/ffmpeg.exe" | cut -d' ' -f1 > "$out/ffmpeg.sha256"

# Generate our own synthetic fixtures with the runner's encoder. No encoder
# binaries or external codec libraries are redistributed in the application.
for codec in rawvideo mjpeg libx264 libx265; do
  case "$codec" in
    rawvideo) name=rawvideo.nut; opts=() ;;
    mjpeg) name=mjpeg.mjpg; opts=(-threads 1) ;;
    libx264) name=h264.h264; opts=(-preset ultrafast -bf 0) ;;
    libx265) name=hevc.hevc; opts=(-preset ultrafast -x265-params bframes=0:pools=1:log-level=error) ;;
  esac
  ffmpeg -hide_banner -loglevel error -y -f lavfi -i testsrc2=size=320x180:rate=30 -t 2 -an -c:v "$codec" "${opts[@]}" "$out/fixtures/$name"
done
