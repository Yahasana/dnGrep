﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ExcelDataReader;
using ExcelNumberFormat;

namespace dnGREP.Engines.OpenXml
{
    internal static class ExcelReader
    {
        public static List<KeyValuePair<string, string>> ExtractExcelText(Stream stream)
        {
            List<KeyValuePair<string, string>> results = new List<KeyValuePair<string, string>>();

            // Auto-detect format, supports:
            //  - Binary Excel files (2.0-2003 format; *.xls)
            //  - OpenXml Excel files (2007 format; *.xlsx)
            using (var reader = ExcelReaderFactory.CreateReader(stream))
            {
                do
                {
                    StringBuilder sb = new StringBuilder();
                    while (reader.Read())
                    {
                        for (int col = 0; col < reader.FieldCount; col++)
                        {
                            sb.Append(GetFormattedValue(reader, col, CultureInfo.CurrentCulture)).Append('\t');
                        }

                        sb.Append(Environment.NewLine);
                    }

                    results.Add(new KeyValuePair<string, string>(reader.Name, sb.ToString()));

                } while (reader.NextResult());

            }

            return results;
        }

        private static string GetFormattedValue(IExcelDataReader reader, int columnIndex, CultureInfo culture)
        {
            string result;
            var value = reader.GetValue(columnIndex);
            var formatString = reader.GetNumberFormatString(columnIndex);
            if (formatString != null)
            {
                var format = new NumberFormat(formatString);
                result = format.Format(value, culture);
            }
            else
            {
                result = Convert.ToString(value, culture);
            }
            return result.Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
