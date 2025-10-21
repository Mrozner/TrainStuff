using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Maui.Graphics;

namespace ClaudeSepareted;

public class ScheduleDrawable : IDrawable
{
    private readonly TimeSpan _start;
    private readonly TimeSpan _end;
    private readonly string _from;
    private readonly string _to;

    public ScheduleDrawable(TimeSpan start, TimeSpan end, string from = "Kiinduló", string to = "Végállomás")
    {
        _start = start;
        _end = end;
        _from = from;
        _to = to;
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        float width = dirtyRect.Width;
        float centerY = dirtyRect.Height / 2;

        float startX = (float)(_start.TotalMinutes / 1440.0 * width);
        float endX = (float)(_end.TotalMinutes / 1440.0 * width);

        // Alapvonal
        canvas.StrokeColor = Colors.Black;
        canvas.StrokeSize = 2;
        canvas.DrawLine(0, centerY, width, centerY);

        // Utazás szakasz
        canvas.StrokeColor = Colors.Blue;
        canvas.StrokeSize = 4;
        canvas.DrawLine(startX, centerY, endX, centerY);

        // Indulási marker
        canvas.FillColor = Colors.Green;
        canvas.FillCircle(startX, centerY, 7);

        // Érkezési marker
        canvas.FillColor = Colors.Red;
        canvas.FillCircle(endX, centerY, 7);

        // Szaggatott segédvonalak felfelé
        //canvas.StrokeColor = Colors.Gray;
        //canvas.StrokeSize = 1;
        //canvas.StrokeDashPattern = new float[] { 4, 4 };
        float top = centerY - 40; // mennyire menjen fel
        //canvas.DrawLine(startX, centerY, startX, top);
        //canvas.DrawLine(endX, centerY, endX, top);

        // --- IMPORTANT: visszaállítjuk a dash-t MIELŐTT a szöveget rajzoljuk
        canvas.StrokeDashPattern = null;

        // DEBUG: rajzold ki a téglalapokat, ahol a feliratoknak meg kéne jelennie
        // (kapcsold ki később, csak debughoz hagyd meg)
        var startRect = new RectF(startX - 30, Math.Max(0, top - 20), 60, 20);
        var endRect = new RectF(endX - 30, Math.Max(0, top - 20), 60, 20);
        // uncomment to visualize bounds:
        // canvas.StrokeColor = Colors.Magenta;
        // canvas.DrawRectangle(startRect);
        // canvas.DrawRectangle(endRect);

        // Idők kiírása
        canvas.FontColor = Colors.Black;
        canvas.FontSize = 14;

        // biztosítsuk, hogy a téglalap Y ne legyen negatív (kilógás miatt), és
        // VerticalAlignment.Bottom használatával a téglalap alsó széle fog a vonal tetejéhez igazodni
        canvas.DrawString(_start.ToString(@"hh\:mm"),
            startRect,
            HorizontalAlignment.Center,
            VerticalAlignment.Bottom);

        canvas.DrawString(_end.ToString(@"hh\:mm"),
            endRect,
            HorizontalAlignment.Center,
            VerticalAlignment.Bottom);

        // Szöveg a pontok alá (külön-külön)
        canvas.FontColor = Colors.Black;
        canvas.FontSize = 14;

        // indulási pont alá
        canvas.DrawString(
            _from,
            new RectF(startX - 50, centerY + 10, 100, 20), // téglalap a zöld kör alatt
            HorizontalAlignment.Center,
            VerticalAlignment.Top
        );

        // érkezési pont alá
        canvas.DrawString(
            _to,
            new RectF(endX - 50, centerY + 10, 100, 20), // téglalap a piros kör alatt
            HorizontalAlignment.Center,
            VerticalAlignment.Top
        );

    }
}
