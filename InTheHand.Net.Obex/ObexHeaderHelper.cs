using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InTheHand.Net.Obex
{
    internal static class ObexHeaderHelper
    {
        internal static string GetLast(string source)
        {
            var split = source.Split(',');
            return split.Last();
        }
    }
}
