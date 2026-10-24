using System.Text.Json.Nodes;
using PixelBridge.Core;

public class CameraTests
{
    [Fact] public void ExposesActualUltraWideAndFront()
    {
        var c = new CameraControls(JsonNode.Parse("""{"cameras":[{"id":"rear","facing":"back","minZoom":0.67,"maxZoom":8,"opticalStabilization":true},{"id":"selfie","facing":"front","minZoom":1,"maxZoom":4}]}""")!.AsObject(), new());
        Assert.Collection(c.Lenses, l => { Assert.Equal("Ultra wide", l.Title); Assert.Equal(.67, l.Zoom); }, l => Assert.Equal("Wide", l.Title), l => Assert.Equal("selfie", l.CameraId));
    }
    [Fact] public void FeedbackDoesNotResetDecoder()
    {
        var previous = JsonNode.Parse("""{"cameraId":"0","width":1280,"height":720,"codec":"h264","appliedZoomRatio":1} """)!.AsObject();
        var next = previous.DeepClone().AsObject(); next["appliedZoomRatio"] = 2; next["rotationDegrees"] = 90;
        Assert.False(CameraControls.FormatChanged(previous, next));
        next["cameraId"] = "1"; Assert.True(CameraControls.FormatChanged(previous, next));
    }
    [Theory] [InlineData("ncm\n",true)] [InlineData("rndis,adb",true)] [InlineData("mtp,adb",false)] [InlineData("",false)]
    public void DetectsUsbSharing(string mode, bool expected) => Assert.Equal(expected, UsbSharing.IsSharing(mode));
    [Theory] [InlineData(0,false,0,1,2,3)] [InlineData(90,false,2,0,3,1)] [InlineData(180,false,3,2,1,0)] [InlineData(270,false,1,3,0,2)]
    [InlineData(0,true,1,0,3,2)] [InlineData(90,true,0,2,1,3)] [InlineData(180,true,2,3,0,1)] [InlineData(270,true,3,1,2,0)]
    public void RotatesAndMirrorsBothPlanes(int rotation, bool mirror, int a, int b, int c, int d)
    {
        const int w = 16, h = 8;
        byte[] colors = [40,80,120,160]; var input = new byte[w*h*3/2];
        for(int y=0;y<h;y++) for(int x=0;x<w;x++) input[y*w+x] = colors[(y<h/2?0:2)+(x<w/2?0:1)];
        for(int y=0;y<h/2;y++) for(int x=0;x<w;x+=2) { var k=(y<h/4?0:2)+(x<w/2?0:1); input[w*h+y*w+x]=(byte)(20+k); input[w*h+y*w+x+1]=(byte)(200+k); }
        var output = new VideoGeometry().Transform(input,w,h,rotation,mirror);
        var xs = rotation is 90 or 270 ? new[]{6,8} : new[]{2,12}; int[] order=[a,b,c,d];
        for(int i=0;i<4;i++) { int x=xs[i%2], y=i<2?2:6; Assert.Equal(colors[order[i]],output[y*w+x]); Assert.Equal(20+order[i],output[w*h+y/2*w+x/2*2]); Assert.Equal(200+order[i],output[w*h+y/2*w+x/2*2+1]); }
        if(rotation is 90 or 270) { Assert.Equal(16,output[0]); Assert.Equal(128,output[w*h]); }
    }
    [Fact] public void DoesNotCopyUnchangedFrames() { var data = new byte[24]; Assert.Same(data,new VideoGeometry().Transform(data,4,4,0,false)); }
    [Fact] public void RejectsMalformedFrames() => Assert.Throws<ArgumentException>(()=>new VideoGeometry().Transform(new byte[1],16,8,0,false));
}
