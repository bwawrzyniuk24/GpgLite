using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GpgLite
{
    // Ikony wstążki rysowane w kodzie (GDI+), przekazywane do Office jako IPictureDisp.
    // Dzięki temu nie zależą od dostępności nazw imageMso w danej wersji Outlooka.
    /// <summary>
    /// Ikony wstążki rysowane w kodzie (GDI+) – nie zależą od dostępności nazw imageMso w danej wersji Office.
    /// </summary>
    internal static class Icons
    {
        static readonly Dictionary<string, object> cache = new Dictionary<string, object>();

        class PictureHost : AxHost
        {
            /// <summary>Konstruktor wymagany przez AxHost (klasa służy tylko do konwersji obrazu).</summary>
            PictureHost() : base("63109182-966B-4e3c-A8B2-8BC4A88D221C") { }
            /// <summary>Zamienia obraz .NET na IPictureDisp wymagany przez wstążkę Office.</summary>
            public static object ToPictureDisp(Image img) { return GetIPictureDispFromPicture(img); }
        }

        /// <summary>Ikona dla przycisku wstążki (IPictureDisp, z pamięci podręcznej).</summary>
        public static object Get(string controlId)
        {
            lock (cache)
            {
                object pic;
                if (cache.TryGetValue(controlId, out pic)) return pic;
                Bitmap bmp;
                try { bmp = Draw(controlId); }
                catch (Exception ex) { Log.Warn("Ikona " + controlId + ": " + ex.Message); return null; }
                pic = bmp == null ? null : PictureHost.ToPictureDisp(bmp);
                cache[controlId] = pic;
                return pic;
            }
        }

        static readonly Color Blue = Color.FromArgb(0, 99, 177);
        static readonly Color Green = Color.FromArgb(16, 124, 16);
        static readonly Color Gold = Color.FromArgb(202, 140, 0);
        static readonly Color Gray = Color.FromArgb(96, 96, 96);

        /// <summary>Rysuje ikonę 32×32 dla identyfikatora przycisku (null = brak ikony).</summary>
        static Bitmap Draw(string id)
        {
            switch (id)
            {
                case "glDecrypt": return Key(Gold);
                case "glEncOnSend": return Lock(Blue);
                case "glSignOnSend": return Seal(Green);
                case "glEncSubject": return SubjectLock(Blue);
                case "glAuto": return Pane(Blue);
                case "glForget": return Trash(Gray);
                case "glKleo": return Kleo(Blue);
                case "glHelp": return Help(Gray);
                default: return null;
            }
        }

        /// <summary>Tworzy przezroczystą bitmapę 32×32 z wygładzaniem do rysowania ikony.</summary>
        static Bitmap NewCanvas(out Graphics g)
        {
            var bmp = new Bitmap(32, 32);
            g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            return bmp;
        }

        /// <summary>Rysuje ikonę kłódki (szyfrowanie).</summary>
        static Bitmap Lock(Color c)
        {
            Graphics g; var bmp = NewCanvas(out g);
            using (g)
            using (var pen = new Pen(c, 3f))
            using (var brush = new SolidBrush(c))
            {
                g.DrawArc(pen, 9, 3, 14, 16, 180, 180);          // pałąk
                g.FillRectangle(brush, 5, 13, 22, 16);             // korpus
                using (var path = RoundRect(5, 13, 22, 16, 3)) g.FillPath(brush, path);
                g.FillEllipse(Brushes.White, 13.5f, 17, 5, 5);     // dziurka
                g.FillRectangle(Brushes.White, 15, 20, 2, 5);
            }
            return bmp;
        }

        /// <summary>Rysuje ikonę klucza (odszyfrowanie).</summary>
        static Bitmap Key(Color c)
        {
            Graphics g; var bmp = NewCanvas(out g);
            using (g)
            using (var pen = new Pen(c, 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawEllipse(new Pen(c, 3.5f), 4, 4, 11, 11);     // główka
                g.DrawLine(pen, 13, 13, 27, 27);                   // trzon
                g.DrawLine(pen, 22, 22, 19, 25);                   // ząbki
                g.DrawLine(pen, 26, 26, 23, 29);
            }
            return bmp;
        }

        /// <summary>Rysuje ikonę pieczęci z haczykiem (podpis).</summary>
        /// <summary>Rysuje ikonę tematu z kłódką (szyfrowanie tematu): wiersz „T” z liniami tekstu i mała kłódka.</summary>
        static Bitmap SubjectLock(Color c)
        {
            Graphics g; var bmp = NewCanvas(out g);
            using (g)
            using (var brush = new SolidBrush(c))
            using (var pen = new Pen(c, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var white = new Pen(Color.White, 2.2f))
            {
                // litera „T” (temat) i linie tekstu
                g.FillRectangle(brush, 3, 4, 14, 3.5f);
                g.FillRectangle(brush, 8.25f, 4, 3.5f, 15);
                g.DrawLine(pen, 3, 23, 13, 23);
                g.DrawLine(pen, 3, 28, 11, 28);
                // kłódka w prawym dolnym rogu
                g.DrawArc(pen, 18.5f, 11, 9, 10, 180, 180);
                using (var path = RoundRect(16, 16, 14, 13, 3)) g.FillPath(brush, path);
                g.DrawLine(white, 23, 20.5f, 23, 24.5f);
            }
            return bmp;
        }

        static Bitmap Seal(Color c)
        {
            Graphics g; var bmp = NewCanvas(out g);
            using (g)
            using (var brush = new SolidBrush(c))
            using (var pen = new Pen(Color.White, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                // „pieczęć” – koło z ząbkami + haczyk
                for (int i = 0; i < 12; i++)
                {
                    double a = i * Math.PI / 6;
                    g.FillEllipse(brush, (float)(16 + 11 * Math.Cos(a)) - 3, (float)(16 + 11 * Math.Sin(a)) - 3, 6, 6);
                }
                g.FillEllipse(brush, 5, 5, 22, 22);
                g.DrawLine(pen, 10.5f, 16.5f, 14.5f, 20.5f);
                g.DrawLine(pen, 14.5f, 20.5f, 22f, 12f);
            }
            return bmp;
        }

        /// <summary>Rysuje ikonę okna z panelem (podgląd automatyczny).</summary>
        static Bitmap Pane(Color c)
        {
            Graphics g; var bmp = NewCanvas(out g);
            using (g)
            using (var pen = new Pen(c, 2.5f))
            using (var brush = new SolidBrush(c))
            {
                g.DrawRectangle(pen, 3, 5, 26, 22);
                g.FillRectangle(brush, 18, 5, 11, 22);              // panel po prawej
                using (var p2 = new Pen(c, 2f)) { g.DrawLine(p2, 6, 11, 14, 11); g.DrawLine(p2, 6, 16, 14, 16); g.DrawLine(p2, 6, 21, 14, 21); }
            }
            return bmp;
        }

        /// <summary>Rysuje ikonę kosza (zapomnij odszyfrowane).</summary>
        static Bitmap Trash(Color c)
        {
            Graphics g; var bmp = NewCanvas(out g);
            using (g)
            using (var pen = new Pen(c, 2.5f))
            using (var brush = new SolidBrush(c))
            {
                g.FillRectangle(brush, 6, 7, 20, 3);
                g.FillRectangle(brush, 12, 4, 8, 3);
                g.DrawRectangle(pen, 8, 11, 16, 17);
                g.DrawLine(pen, 13, 14, 13, 25);
                g.DrawLine(pen, 19, 14, 19, 25);
            }
            return bmp;
        }

        /// <summary>Rysuje ikonę listy kluczy (Kleopatra).</summary>
        static Bitmap Kleo(Color c)
        {
            Graphics g; var bmp = NewCanvas(out g);
            using (g)
            using (var pen = new Pen(c, 2.5f))
            using (var brush = new SolidBrush(c))
            {
                g.DrawRectangle(pen, 4, 4, 24, 24);                 // „menedżer kluczy”: karta z listą kluczy
                for (int y = 9; y <= 23; y += 7)
                {
                    g.FillEllipse(brush, 8, y - 2, 5, 5);
                    g.FillRectangle(brush, 15, y - 1, 9, 2.5f);
                }
            }
            return bmp;
        }

        /// <summary>Rysuje ikonę znaku zapytania (pomoc).</summary>
        static Bitmap Help(Color c)
        {
            Graphics g; var bmp = NewCanvas(out g);
            using (g)
            using (var pen = new Pen(c, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var brush = new SolidBrush(c))
            {
                g.DrawEllipse(new Pen(c, 2.5f), 3, 3, 26, 26);
                g.DrawArc(pen, 10, 8, 12, 10, 190, 200);
                g.DrawLine(pen, 16, 18, 16, 21);
                g.FillEllipse(brush, 14, 23.5f, 4, 4);
            }
            return bmp;
        }

        /// <summary>Tworzy ścieżkę prostokąta z zaokrąglonymi rogami.</summary>
        static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath();
            p.AddArc(x, y, 2 * r, 2 * r, 180, 90);
            p.AddArc(x + w - 2 * r, y, 2 * r, 2 * r, 270, 90);
            p.AddArc(x + w - 2 * r, y + h - 2 * r, 2 * r, 2 * r, 0, 90);
            p.AddArc(x, y + h - 2 * r, 2 * r, 2 * r, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
