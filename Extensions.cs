using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace EasyFlow
{
    static class Extensions
    {
        public static bool IsWildcard(this string str)
        {
            return str.Contains('*');
        }
    }
}
