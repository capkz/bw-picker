# Rebuild the Windows icon from its vector geometry; no external dependencies.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
public static class PickerIconBuilder {
    static GraphicsPath Polygon(PointF[] points) {
        var path = new GraphicsPath(); path.AddPolygon(points); return path;
    }
    public static void Build(string directory) {
        using (var source = new Bitmap(1024, 1024, PixelFormat.Format32bppArgb)) {
            using (var g = Graphics.FromImage(source)) {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.ScaleTransform(32, 32);
                using (var tile = new GraphicsPath()) {
                    tile.AddArc(1, 1, 14, 14, 180, 90);
                    tile.AddArc(17, 1, 14, 14, 270, 90);
                    tile.AddArc(17, 17, 14, 14, 0, 90);
                    tile.AddArc(1, 17, 14, 14, 90, 90); tile.CloseFigure();
                    using (var blue = new LinearGradientBrush(new PointF(0, 1), new PointF(0, 31), Color.FromArgb(49,137,255), Color.FromArgb(23,93,220))) g.FillPath(blue, tile);
                }
                using (var shield = new GraphicsPath()) {
                    shield.AddLines(new [] { new PointF(14,5), new PointF(23,8), new PointF(23,15) });
                    shield.AddBezier(23,15, 23,20, 18,24, 14,26);
                    shield.AddBezier(14,26, 10,24, 5,20, 5,15);
                    shield.AddLine(5,15,5,8); shield.CloseFigure();
                    g.FillPath(Brushes.White, shield);
                }
                using (var half = new GraphicsPath()) {
                    half.AddLine(14,8,14,22); half.AddBezier(14,22,11,20,8,17,8,14);
                    half.AddLine(8,14,8,10); half.CloseFigure();
                    using (var blue = new SolidBrush(Color.FromArgb(33,111,233))) g.FillPath(blue,half);
                }
                using (var cursor = Polygon(new [] { new PointF(19,16), new PointF(29,24), new PointF(24,25), new PointF(22,30), new PointF(19,29), new PointF(21,24), new PointF(17,25) })) {
                    using (var navy = new SolidBrush(Color.FromArgb(19,70,164))) g.FillPath(navy,cursor);
                    using (var outline = new Pen(Color.White,1.5f) { LineJoin = LineJoin.Round }) g.DrawPath(outline,cursor);
                }
            }
            int[] sizes = {16,20,24,32,40,48,64,128,256};
            byte[][] frames = new byte[sizes.Length][];
            for (int i=0; i<sizes.Length; i++) {
                using (var image = new Bitmap(sizes[i], sizes[i], PixelFormat.Format32bppArgb)) {
                    using (var g = Graphics.FromImage(image)) {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(source,new Rectangle(0,0,sizes[i],sizes[i]));
                    }
                    using (var memory = new MemoryStream()) { image.Save(memory,ImageFormat.Png); frames[i]=memory.ToArray(); }
                    if (sizes[i]==256) image.Save(Path.Combine(directory,"bw-picker.png"),ImageFormat.Png);
                    if (sizes[i]==16 || sizes[i]==32) image.Save(Path.Combine(directory,"bw-picker-"+sizes[i]+".png"),ImageFormat.Png);
                }
            }
            using (var writer = new BinaryWriter(File.Create(Path.Combine(directory,"bw-picker.ico")))) {
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
                int offset=6+16*sizes.Length;
                for (int i=0;i<sizes.Length;i++) {
                    writer.Write((byte)(sizes[i]==256?0:sizes[i])); writer.Write((byte)(sizes[i]==256?0:sizes[i]));
                    writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
                    writer.Write(frames[i].Length); writer.Write(offset); offset+=frames[i].Length;
                }
                foreach (var frame in frames) writer.Write(frame);
            }
        }
    }
}
'@
[PickerIconBuilder]::Build($PSScriptRoot)
