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
        // 1. Define fixed padding to leave room for text on the edges
        float padding = 60f;

        // 2. Set Start and End X coordinates to strictly use the bounds
        float startX = dirtyRect.Left + padding;
        float endX = dirtyRect.Right - padding;

        // 3. Center the Y coordinate
        float yCenter = dirtyRect.Center.Y;

        // Alapvonal
        canvas.StrokeColor = Colors.Black;
        canvas.StrokeSize = 2;
        canvas.DrawLine(dirtyRect.Left, yCenter, dirtyRect.Right, yCenter);

        // Utazás szakasz
        canvas.StrokeColor = Colors.Blue;
        canvas.StrokeSize = 4;
        canvas.DrawLine(startX, yCenter, endX, yCenter);

        // Indulási marker
        canvas.FillColor = Colors.Green;
        canvas.FillCircle(startX, yCenter, 7);

        // Érkezési marker
        canvas.FillColor = Colors.Red;
        canvas.FillCircle(endX, yCenter, 7);

        // Szaggatott segédvonalak felfelé
        //canvas.StrokeColor = Colors.Gray;
        //canvas.StrokeSize = 1;
        //canvas.StrokeDashPattern = new float[] { 4, 4 };
        float top = yCenter - 40; // mennyire menjen fel
        //canvas.DrawLine(startX, yCenter, startX, top);
        //canvas.DrawLine(endX, yCenter, endX, top);

        // --- IMPORTANT: visszaállítjuk a dash-t MIELŐTT a szöveget rajzoljuk
        canvas.StrokeDashPattern = null;

        // DEBUG: rajzold ki a téglalapokat, ahol a feliratoknak meg kéne jelennie
        // (kapcsold ki később, csak debughoz hagyd meg)
        var startRect = new RectF(startX - 50, Math.Max(0, top - 20), 50, 20);
        var endRect = new RectF(endX, Math.Max(0, top - 20), 50, 20);
        // uncomment to visualize bounds:
        // canvas.StrokeColor = Colors.Magenta;
        // canvas.DrawRectangle(startRect);
        // canvas.DrawRectangle(endRect);

        // Idők kiírása
        canvas.FontColor = Colors.Black;
        canvas.FontSize = 14;

        // indulási idő - jobbra igazítva az startX pont mögé
        canvas.DrawString(_start.ToString(@"hh\:mm"),
            startRect,
            HorizontalAlignment.Right,
            VerticalAlignment.Bottom);

        // érkezési idő - balra igazítva az endX pont elé
        canvas.DrawString(_end.ToString(@"hh\:mm"),
            endRect,
            HorizontalAlignment.Left,
            VerticalAlignment.Bottom);

        // Szöveg a pontok alá (külön-külön)
        canvas.FontColor = Colors.Black;
        canvas.FontSize = 14;

        // indulási pont alá - jobbra igazítva
        canvas.DrawString(
            _from,
            new RectF(startX - 50, yCenter + 10, 50, 20), // téglalap a zöld kör alatt
            HorizontalAlignment.Right,
            VerticalAlignment.Top
        );

        // érkezési pont alá - balra igazítva
        canvas.DrawString(
            _to,
            new RectF(endX, yCenter + 10, 50, 20), // téglalap a piros kör alatt
            HorizontalAlignment.Left,
            VerticalAlignment.Top
        );

    }
}