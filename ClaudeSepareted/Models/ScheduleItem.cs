using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Maui.Graphics;

namespace ClaudeSepareted;

public class ScheduleItem
{
    public string TrainName { get; set; }
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public string From { get; set; }
    public string To { get; set; }


    public IDrawable Drawable => new ScheduleDrawable(Start, End, From, To);
}


