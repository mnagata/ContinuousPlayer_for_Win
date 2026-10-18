# Recreates the Android foreground vector using System.Drawing (no external tools).
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\ContinuousPlayer\Assets'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing.Common
Add-Type -ReferencedAssemblies System.Drawing.Common,System.Drawing.Primitives,System.Private.Windows.GdiPlus,System.Private.Windows.Core -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
public static class BrandAssets {
  static GraphicsPath Back() {
    var p = new GraphicsPath(); p.AddLine(46,43,46,29); p.AddBezier(46,29,46,27,47,26,49,27);
    p.AddLine(49,27,76,32); p.AddBezier(76,32,78,32.3f,79,33.7f,79,36); p.AddLine(79,36,79,55); return p;
  }
  static GraphicsPath Front() {
    var p = new GraphicsPath(); p.AddLine(33,68,33,39); p.AddBezier(33,39,33,37,34,36,36,37);
    p.AddLine(36,37,65,42); p.AddBezier(65,42,67,42.3f,68,43.7f,68,46); p.AddLine(68,46,68,66); return p;
  }
  static GraphicsPath Arrow() {
    var p = new GraphicsPath(); p.AddBezier(30,60,24,77,44,87,74,64);
    p.AddLines(new PointF[]{new PointF(74,64),new PointF(68,60),new PointF(82,60),new PointF(78,74),new PointF(76,68)});
    p.AddBezier(76,68,49,90,20,79,30,60); p.CloseFigure(); return p;
  }
  public static Bitmap Draw(int width,int height) {
    var bitmap = new Bitmap(width,height); using(var g = Graphics.FromImage(bitmap)) {
      g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.FromArgb(16,23,67));
      float scale = Math.Min(width,height)/108f; g.TranslateTransform((width-108*scale)/2,(height-108*scale)/2); g.ScaleTransform(scale,scale);
      using(var back=Back()) using(var front=Front()) using(var arrow=Arrow())
      using(var b=new LinearGradientBrush(new Point(46,28),new Point(79,52),Color.FromArgb(148,116,255),Color.FromArgb(255,142,219)))
      using(var f=new LinearGradientBrush(new Point(33,37),new Point(68,68),Color.FromArgb(54,229,245),Color.FromArgb(52,138,255)))
      using(var a=new LinearGradientBrush(new Point(28,77),new Point(80,60),Color.FromArgb(24,217,242),Color.FromArgb(255,158,228)))
      using(var bp=new Pen(b,5)) using(var fp=new Pen(f,5)) {
        bp.StartCap=bp.EndCap=fp.StartCap=fp.EndCap=LineCap.Round; bp.LineJoin=fp.LineJoin=LineJoin.Round;
        g.DrawPath(bp,back); g.DrawPath(fp,front); g.FillPolygon(Brushes.White,new PointF[]{new PointF(44,47),new PointF(44,65),new PointF(59,55.5f)}); g.FillPath(a,arrow);
      }
    } return bitmap;
  }
  public static void Generate(string directory) {
    Directory.CreateDirectory(directory);
    string[] names={"SplashScreen.scale-200.png","LockScreenLogo.scale-200.png","Square150x150Logo.scale-200.png","Square44x44Logo.scale-200.png","Square44x44Logo.targetsize-24_altform-unplated.png","Square44x44Logo.targetsize-48_altform-lightunplated.png","StoreLogo.png","Wide310x150Logo.scale-200.png"};
    int[] widths={1240,48,300,88,24,48,50,620}; int[] heights={600,48,300,88,24,48,50,300};
    for(int i=0;i<names.Length;i++) using(var bitmap=Draw(widths[i],heights[i])) bitmap.Save(Path.Combine(directory,names[i]),ImageFormat.Png);
    int[] sizes={16,24,32,48,64,256}; byte[][] images=new byte[sizes.Length][];
    for(int i=0;i<sizes.Length;i++) using(var bitmap=Draw(sizes[i],sizes[i])) using(var stream=new MemoryStream()) { bitmap.Save(stream,ImageFormat.Png); images[i]=stream.ToArray(); }
    using(var writer=new BinaryWriter(File.Create(Path.Combine(directory,"AppIcon.ico")))) {
      writer.Write((ushort)0);writer.Write((ushort)1);writer.Write((ushort)sizes.Length); int offset=6+16*sizes.Length;
      for(int i=0;i<sizes.Length;i++) { writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)0);writer.Write((byte)0);writer.Write((ushort)1);writer.Write((ushort)32);writer.Write(images[i].Length);writer.Write(offset);offset+=images[i].Length; }
      foreach(var image in images)writer.Write(image);
    }
  }
}
'@
[BrandAssets]::Generate([IO.Path]::GetFullPath($OutputDirectory))
Write-Host "Generated Android-compatible brand assets in $OutputDirectory"
