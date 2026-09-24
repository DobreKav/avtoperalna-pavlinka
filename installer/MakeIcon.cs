// Draws the Автоперална Павлинка icon (car + water drops) and writes a multi-size .ico
// with PNG frames, plus a 512 px PNG. Used by installer\build.bat:
//   MakeIcon.exe <out.ico> <out.png>
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class MakeIcon
{
    static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        GraphicsPath p = new GraphicsPath();
        p.AddArc(x, y, r * 2, r * 2, 180, 90);
        p.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        p.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        p.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        p.CloseFigure();
        return p;
    }

    // Water drop with its tip at (cx, top) and a round bottom of radius r.
    static GraphicsPath Drop(float cx, float top, float r)
    {
        float cy = top + r * 2.1f;
        GraphicsPath p = new GraphicsPath();
        p.AddBezier(cx, top, cx + r * 0.25f, top + r * 0.9f, cx + r, cy - r * 0.6f, cx + r, cy);
        p.AddArc(cx - r, cy - r, r * 2, r * 2, 0, 180);
        p.AddBezier(cx - r, cy, cx - r, cy - r * 0.6f, cx - r * 0.25f, top + r * 0.9f, cx, top);
        p.CloseFigure();
        return p;
    }

    static Bitmap Draw(int size)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            float s = size;
            bool tiny = size <= 24;

            // Tile
            using (GraphicsPath tile = RoundRect(s * 0.02f, s * 0.02f, s * 0.96f, s * 0.96f, s * 0.2f))
            using (LinearGradientBrush bg = new LinearGradientBrush(new PointF(0, 0), new PointF(0, s),
                       Color.FromArgb(0x29, 0xB6, 0xF6), Color.FromArgb(0x0D, 0x47, 0xA1)))
                g.FillPath(bg, tile);

            // Water drops above the car
            using (SolidBrush drop = new SolidBrush(Color.FromArgb(0xE1, 0xF5, 0xFE)))
            {
                if (tiny)
                {
                    using (GraphicsPath d = Drop(s * 0.5f, s * 0.10f, s * 0.13f)) g.FillPath(drop, d);
                }
                else
                {
                    using (GraphicsPath d = Drop(s * 0.30f, s * 0.14f, s * 0.075f)) g.FillPath(drop, d);
                    using (GraphicsPath d = Drop(s * 0.50f, s * 0.08f, s * 0.095f)) g.FillPath(drop, d);
                    using (GraphicsPath d = Drop(s * 0.70f, s * 0.14f, s * 0.075f)) g.FillPath(drop, d);
                }
            }

            // Foam bubbles
            if (!tiny)
            {
                using (SolidBrush bubble = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
                {
                    g.FillEllipse(bubble, s * 0.13f, s * 0.38f, s * 0.07f, s * 0.07f);
                    g.FillEllipse(bubble, s * 0.80f, s * 0.36f, s * 0.08f, s * 0.08f);
                    g.FillEllipse(bubble, s * 0.86f, s * 0.47f, s * 0.045f, s * 0.045f);
                }
            }

            // Car
            float top = tiny ? 0.52f : 0.47f;
            using (SolidBrush white = new SolidBrush(Color.White))
            {
                using (GraphicsPath cabin = new GraphicsPath())
                {
                    cabin.AddPolygon(new[]
                    {
                        new PointF(s * 0.30f, s * (top + 0.14f)),
                        new PointF(s * 0.37f, s * top),
                        new PointF(s * 0.63f, s * top),
                        new PointF(s * 0.72f, s * (top + 0.14f)),
                    });
                    g.FillPath(white, cabin);
                }
                using (GraphicsPath body = RoundRect(s * 0.14f, s * (top + 0.12f), s * 0.72f, s * 0.17f, s * 0.06f))
                    g.FillPath(white, body);
            }
            if (!tiny)
            {
                using (SolidBrush glass = new SolidBrush(Color.FromArgb(0x4F, 0xC3, 0xF7)))
                {
                    g.FillPolygon(glass, new[]
                    {
                        new PointF(s * 0.35f, s * (top + 0.125f)), new PointF(s * 0.395f, s * (top + 0.03f)),
                        new PointF(s * 0.49f, s * (top + 0.03f)), new PointF(s * 0.49f, s * (top + 0.125f)),
                    });
                    g.FillPolygon(glass, new[]
                    {
                        new PointF(s * 0.52f, s * (top + 0.125f)), new PointF(s * 0.52f, s * (top + 0.03f)),
                        new PointF(s * 0.61f, s * (top + 0.03f)), new PointF(s * 0.665f, s * (top + 0.125f)),
                    });
                }
            }
            float wr = s * (tiny ? 0.085f : 0.075f);
            using (SolidBrush tyre = new SolidBrush(Color.FromArgb(0x0D, 0x2B, 0x5E)))
            using (SolidBrush hub = new SolidBrush(Color.FromArgb(0xB3, 0xE5, 0xFC)))
            {
                foreach (float cx in new[] { 0.31f, 0.69f })
                {
                    float x = s * cx, y = s * (top + 0.29f);
                    g.FillEllipse(tyre, x - wr, y - wr, wr * 2, wr * 2);
                    if (!tiny) g.FillEllipse(hub, x - wr * 0.42f, y - wr * 0.42f, wr * 0.84f, wr * 0.84f);
                }
            }
        }
        return bmp;
    }

    static byte[] Png(Bitmap b)
    {
        using (MemoryStream m = new MemoryStream())
        {
            b.Save(m, ImageFormat.Png);
            return m.ToArray();
        }
    }

    static void Main(string[] args)
    {
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        List<byte[]> frames = new List<byte[]>();
        foreach (int size in sizes) using (Bitmap b = Draw(size)) frames.Add(Png(b));

        using (FileStream f = File.Create(args[0]))
        using (BinaryWriter w = new BinaryWriter(f))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(frames[i].Length); w.Write(offset);
                offset += frames[i].Length;
            }
            foreach (byte[] frame in frames) w.Write(frame);
        }
        if (args.Length > 1) using (Bitmap big = Draw(512)) big.Save(args[1], ImageFormat.Png);
    }
}
