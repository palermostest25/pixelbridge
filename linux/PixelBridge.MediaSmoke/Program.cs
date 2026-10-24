using System.Diagnostics;
using PixelBridge.Linux.Media;

var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
foreach(var a in new[]{"-hide_banner","-loglevel","error","-f","lavfi","-i","testsrc2=size=320x180:rate=15","-frames:v","45","-c:v","libx264","-preset","ultrafast","-tune","zerolatency","-x264-params","keyint=1:repeat-headers=1:aud=1","-f","h264","pipe:1"}) psi.ArgumentList.Add(a);
using var encoder = Process.Start(psi)!;
using var data = new MemoryStream(); var errors = encoder.StandardError.ReadToEndAsync();
await encoder.StandardOutput.BaseStream.CopyToAsync(data); await encoder.WaitForExitAsync();
if(encoder.ExitCode != 0) throw new Exception(await errors);
var bytes=data.ToArray();
// Split at H.264 access-unit delimiters so this exercises the actual bounded frame path.
var starts=new List<int>();
for(int i=0;i+5<bytes.Length;i++) if(bytes[i]==0 && bytes[i+1]==0 && bytes[i+2]==0 && bytes[i+3]==1 && (bytes[i+4]&31)==9) starts.Add(i);
starts.Add(bytes.Length);
if(starts.Count<40) throw new Exception("Encoder fixture has no access unit delimiters");
foreach(var rotation in new[]{0,90,180,270}) foreach(var mirror in new[]{false,true})
{
    using var video=new FfmpegVideo(); int frames=0;
    video.PreviewFrame+=(frame,w,h)=> { if(frame.Length!=w*h*4 || w!=640 || h!=360) throw new Exception("Wrong preview geometry"); Interlocked.Increment(ref frames); };
    video.Start(320,180,false,rotation,mirror);
    for(int i=0;i<starts.Count-1;i++) { video.Feed(bytes[starts[i]..starts[i+1]],true,false); await Task.Delay(50); }
    await Task.Delay(500);
    if(frames<10) throw new Exception($"rotation={rotation} mirror={mirror}: received only {frames} frames");
    Console.WriteLine($"PASS rotation={rotation} mirror={mirror}: {frames} decoded frames");
}
