using ClosedXML.Excel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace BaseProjejct
{
    public class ExcelHelper : IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, int dwFlags);

        private const int MOVEFILE_REPLACE_EXISTING = 0x00000001;
        private const int MOVEFILE_WRITE_THROUGH = 0x00000008;

        private XLWorkbook? _workbook;
        private IXLWorksheet? _worksheet;
        private string _filePath = "";
        private string? _tempFilePath;
        private bool _disposed = false;
        private int _headerRow = 1;

        public int HeaderRow
        {
            get => _headerRow;
            set => _headerRow = value;
        }

        public ExcelHelper()
        {
        }

        public ExcelHelper(string filePath)
        {
            LoadExcel(filePath);
        }

        public void LoadExcel(string filePath)
        {
            _filePath = filePath;
            _worksheet = null;

            for (int i = 0; i < 3; i++)
            {
                try
                {
                    _workbook = new XLWorkbook(filePath);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(500);
                }
                catch (KeyNotFoundException)
                {
                    Thread.Sleep(500);
                }
            }

            try
            {
                _tempFilePath = Path.GetTempFileName() + ".xlsx";
                File.Copy(filePath, _tempFilePath, true);
                _workbook = new XLWorkbook(_tempFilePath);
            }
            catch
            {
                if (_tempFilePath != null && File.Exists(_tempFilePath))
                    File.Delete(_tempFilePath);
                throw;
            }
        }

        public void LoadExcelReadOnly(string filePath)
        {
            LoadExcel(filePath);
        }

        public void Save()
        {
            if (_workbook == null)
                throw new InvalidOperationException("No workbook loaded.");

            try
            {
                if (_worksheet != null)
                {
                    var mergedRanges = _worksheet.MergedRanges.ToList();
                    foreach (var range in mergedRanges)
                    {
                        range.Unmerge();
                    }
                }
            }
            catch { }

            if (!string.IsNullOrEmpty(_tempFilePath))
            {
                _workbook.Save();

                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        _workbook.Dispose();
                        File.Copy(_tempFilePath, _filePath, true);
                        File.Delete(_tempFilePath);
                        _tempFilePath = null;
                        return;
                    }
                    catch (IOException)
                    {
                        string backupPath = _filePath + ".bak.xlsx";
                        _workbook = new XLWorkbook(_tempFilePath);
                        try
                        {
                            if (_worksheet != null)
                            {
                                var mergedRanges = _worksheet.MergedRanges.ToList();
                                foreach (var range in mergedRanges)
                                {
                                    range.Unmerge();
                                }
                            }
                        }
                        catch { }
                        _workbook.SaveAs(backupPath);
                        //_workbook.Dispose();
                        File.Delete(_tempFilePath);
                        _tempFilePath = null;
                        throw new IOException($"文件被占用，已保存到备份文件: {backupPath}");
                    }
                }
            }
            else
            {
                _workbook.Save();
            }
        }

        public string? SaveToBackup()
        {
            if (_workbook == null)
                throw new InvalidOperationException("No workbook loaded.");

            string backupPath = _filePath + "." + DateTime.Now.ToString("yyyyMMddHHmmss") + ".xlsx";
            _workbook.SaveAs(backupPath);
            return backupPath;
        }

        private int GetColumnNumber(string colName)
        {
            if (string.IsNullOrEmpty(colName))
                throw new ArgumentException("Column name cannot be empty.");

            colName = colName.Trim();
            bool isEnglish = true;
            foreach (char c in colName)
            {
                if (c < 'A' || (c > 'Z' && c < 'a') || c > 'z')
                {
                    isEnglish = false;
                    break;
                }
            }

            if (isEnglish)
            {
                colName = colName.ToUpperInvariant();
                int columnNumber = 0;
                foreach (char c in colName)
                {
                    columnNumber = columnNumber * 26 + (c - 'A' + 1);
                }
                if (columnNumber < 1 || columnNumber > 16384)
                    throw new ArgumentOutOfRangeException(nameof(colName), $"Column number must be between 1 and 16384. Got: {columnNumber}");
                return columnNumber;
            }

            return FindColumnByHeader(colName, _headerRow);
        }

        private int FindColumnByHeader(string headerName, int headerRow = 1)
        {
            if (_worksheet == null)
                throw new InvalidOperationException("Please call LoadSheet first.");

            var usedRange = _worksheet.RangeUsed();
            if (usedRange == null)
                throw new InvalidOperationException("No data range found in worksheet.");

            int colCount = usedRange.ColumnCount();
            for (int col = 1; col <= colCount; col++)
            {
                string headerValue = _worksheet.Cell(headerRow, col).GetString().Trim();
                if (string.Equals(headerValue, headerName, StringComparison.OrdinalIgnoreCase))
                {
                    return col;
                }
            }

            throw new ArgumentException($"Column header '{headerName}' not found in row {headerRow}.");
        }

        public List<string> GetSheetNames()
        {
            var sheetNames = new List<string>();
            if (_workbook != null)
            {
                foreach (var sheet in _workbook.Worksheets)
                {
                    //Console.WriteLine(sheet.Name);
                    sheetNames.Add(sheet.Name);
                }
            }
            return sheetNames;
        }

        public List<string> GetHeaders()
        {
            var headers = new List<string>();
            if (_worksheet == null)
                throw new InvalidOperationException("Please call LoadSheet first.");

            var usedRange = _worksheet.RangeUsed();
            if (usedRange == null) return headers;

            int colCount = usedRange.ColumnCount();
            for (int col = 1; col <= colCount; col++)
            {
                string headerValue = usedRange.FirstRow().Cell(col).GetString().Trim();
                headers.Add(headerValue);
            }
            return headers;
        }

        public void LoadSheet(string sheetName)
        {
            if (_workbook == null)
                throw new InvalidOperationException("Please call LoadExcel first.");

            _worksheet = _workbook.Worksheets.FirstOrDefault(w => w.Name.Equals(sheetName, StringComparison.OrdinalIgnoreCase));
            if (_worksheet == null)
                throw new ArgumentException($"Sheet '{sheetName}' not found.");
        }

        public int GetRowCount()
        {
            if (_worksheet == null)
                throw new InvalidOperationException("Please call LoadSheet first.");

            return _worksheet.LastRowUsed()?.RowNumber() ?? 0;
        }

        public int GetColumnCount()
        {
            if (_worksheet == null)
                throw new InvalidOperationException("Please call LoadSheet first.");

            return _worksheet.LastColumnUsed()?.ColumnNumber() ?? 0;
        }

        public string? ReadCell(string cellRef)
        {
            if (_worksheet == null)
                throw new InvalidOperationException("Please call LoadSheet first.");

            var cell = _worksheet.Cell(cellRef);
            return cell.GetString();
        }

        public string? ReadCell(int col, int row)
        {
            if (_worksheet == null)
                throw new InvalidOperationException("Please call LoadSheet first.");

            var cell = _worksheet.Cell(row, col);
            return cell.GetString();
        }

        public string? ReadCell(string colName, int row)
        {
            int col = GetColumnNumber(colName);
            return ReadCell(col, row);
        }

        public Dictionary<string, object?> ReadRow(int row)
        {
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (_worksheet == null)
                throw new InvalidOperationException("Please call LoadSheet first.");

            var usedRange = _worksheet.RangeUsed();
            if (usedRange == null) return result;

            int colCount = usedRange.ColumnCount();
            var colNameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int col = 1; col <= colCount; col++)
            {
                string colName = usedRange.FirstRow().Cell(col).GetString();
                object? cellValue = _worksheet.Cell(row, col).Value;

                if (colNameCounts.ContainsKey(colName))
                {
                    colNameCounts[colName]++;
                    colName = $"{colName}_{colNameCounts[colName]}";
                }
                else
                {
                    colNameCounts[colName] = 1;
                }

                result[colName] = cellValue;
            }

            return result;
        }

        public List<Dictionary<string, object?>> ReadAllRows()
        {
            var result = new List<Dictionary<string, object?>>();
            if (_worksheet == null)
                throw new InvalidOperationException("Please call LoadSheet first.");

            var usedRange = _worksheet.RangeUsed();
            if (usedRange == null) return result;

            int rowCount = usedRange.RowCount();
            int colCount = usedRange.ColumnCount();

            string[] colNames = new string[colCount];
            var colNameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int col = 1; col <= colCount; col++)
            {
                string colName = usedRange.FirstRow().Cell(col).GetString();
                if (colNameCounts.ContainsKey(colName))
                {
                    colNameCounts[colName]++;
                    colName = $"{colName}_{colNameCounts[colName]}";
                }
                else
                {
                    colNameCounts[colName] = 1;
                }
                colNames[col - 1] = colName;
            }

            for (int row = 2; row <= rowCount; row++)
            {
                var rowData = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (int col = 1; col <= colCount; col++)
                {
                    string colName = colNames[col - 1];
                    object? cellValue = _worksheet.Cell(row, col).Value;
                    rowData[colName] = cellValue;
                }
                result.Add(rowData);
            }

            return result;
        }

        public void WriteCell(string cellRef, object value)
        {
            if (_worksheet == null)
                throw new InvalidOperationException("Please call LoadSheet first.");

            _worksheet.Cell(cellRef).Value = value?.ToString() ?? "";
        }

        public void WriteCell(int col, int row, object value)
        {
            if (_worksheet == null)
                throw new InvalidOperationException("Please call LoadSheet first.");

            _worksheet.Cell(row, col).Value = value?.ToString() ?? "";
        }

        public void WriteCell(string colName, int row, object value)
        {
            int col = GetColumnNumber(colName);
            WriteCell(col, row, value);
        }

        public void WriteTestResult(string colName, int row, string result)
        {
            WriteCell(colName, row, result);
        }

        public void WriteTestResult(int col, int row, string result)
        {
            WriteCell(col, row, result);
        }

        public void SaveAs(string newPath)
        {
            if (_workbook == null)
                throw new InvalidOperationException("No workbook loaded.");

            _workbook.SaveAs(newPath);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    if (!string.IsNullOrEmpty(_tempFilePath) && File.Exists(_tempFilePath))
                    {
                        try { File.Delete(_tempFilePath); } catch { }
                    }
                    _worksheet = null;
                    _workbook?.Dispose();
                    _workbook = null;
                }
                _disposed = true;
            }
        }

        ~ExcelHelper()
        {
            Dispose(false);
        }
    }
}
