// 统一的最小 using 集合（UseWindowsForms 会把 System.Drawing / System.Windows.Forms
// 塞进隐式 using，导致和 WPF 类型大面积二义性，所以关掉隐式 using 后显式声明）。
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Net.Http;
global using System.Threading;
global using System.Threading.Tasks;
