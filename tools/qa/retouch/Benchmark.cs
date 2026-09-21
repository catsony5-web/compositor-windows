using Compositor.Windows;
using System.Windows;
using System.Windows.Media;
using System.Diagnostics;
public static class Benchmark {
[STAThread] public static void Main() {
var layer=new Layer {Pixels=Raster.Solid(4096,4096,Colors.Gray)};
var samples=Enumerable.Range(1,32).Select(i=>new Point(1000+i*10,1000+i*2)).ToArray();
foreach(var kind in new[]{RetouchKind.Blur,RetouchKind.Smudge,RetouchKind.Clone}) {GC.Collect(); var before=GC.GetAllocatedBytesForCurrentThread();var clock=Stopwatch.StartNew();var result=RetouchTools.ApplyStroke(layer,kind,layer.Pixels,new Point(500,500),new Point(1000,1000),new Point(1000,1000),samples,21,.7,.7);clock.Stop();Console.WriteLine($"{kind}: 4096x4096,32 dabs,42px brush: {clock.ElapsedMilliseconds}ms, {(GC.GetAllocatedBytesForCurrentThread()-before)/1024.0/1024:0.0}MiB allocated; original remains {layer.Pixels.Data[3]}");}
}
}
