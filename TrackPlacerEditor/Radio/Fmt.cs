using System.Globalization;

namespace TrackPlacer.Radio
{
	internal static class Fmt
	{
		private static readonly CultureInfo De = CreateDe();

		private static CultureInfo CreateDe()
		{
			try { return CultureInfo.GetCultureInfo("de-DE"); }
			catch { return CultureInfo.InvariantCulture; }
		}

		public static string M(float v, int digits = 1) => v.ToString("F" + digits, Tr.German ? De : CultureInfo.InvariantCulture);
	}
}
