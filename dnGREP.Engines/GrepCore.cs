using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using NLog;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.XPath;
using dnGREP.Common;
using dnGREP.Engines;

namespace dnGREP.Common
{
	public class GrepCore
	{
		private GrepEngineInitParams searchParams = new GrepEngineInitParams();

		public GrepEngineInitParams SearchParams
		{
			get { return searchParams; }
			set { searchParams = value; }
		}
		
		private bool previewFilesDuringSearch = false;

		public bool PreviewFilesDuringSearch
		{
			get { return previewFilesDuringSearch; }
			set { previewFilesDuringSearch = value; }
		}
		
		private static Logger logger = LogManager.GetCurrentClassLogger();
		private List<GrepSearchResult> searchResults = new List<GrepSearchResult>();
		public delegate void SearchProgressHandler(object sender, ProgressStatus files);
		public event SearchProgressHandler ProcessedFile;
		public class ProgressStatus
		{
			public ProgressStatus(int total, int processed, List<GrepSearchResult> results)
			{
				TotalFiles = total;
				ProcessedFiles = processed;
				SearchResults = results;
			}
			public int TotalFiles;
			public int ProcessedFiles;
			public List<GrepSearchResult> SearchResults;
		}

		private static bool cancelProcess = false;

		public static bool CancelProcess
		{
			get { return GrepCore.cancelProcess; }
			set { GrepCore.cancelProcess = value; }
		}
	
		/// <summary>
		/// Searches folder for files whose content matches regex
		/// </summary>
		/// <param name="files">Files to search in. If one of the files does not exist or is open, it is skipped.</param>
		/// <param name="searchRegex">Regex pattern</param>
		/// <returns>List of results. If nothing is found returns empty list</returns>
		public List<GrepSearchResult> Search(string[] files, SearchType searchType, string searchPattern, GrepSearchOption searchOptions, int codePage)
		{
			List<GrepSearchResult> searchResults = new List<GrepSearchResult>();

			if (files == null || files.Length == 0)
				return searchResults;

            GrepCore.CancelProcess = false;

			if (searchPattern == null || searchPattern.Trim() == "")
			{
				foreach (string file in files)
				{
					searchResults.Add(new GrepSearchResult(file, null));
				}

				if (ProcessedFile != null)
				{
					if (PreviewFilesDuringSearch)
						ProcessedFile(this, new ProgressStatus(searchResults.Count, searchResults.Count, searchResults));
					else
						ProcessedFile(this, new ProgressStatus(searchResults.Count, searchResults.Count, null));
				}

				return searchResults;
			}

			int totalFiles = files.Length;
			int processedFiles = 0;
			
			try
			{
				foreach (string file in files)
				{
					try
					{
						IGrepEngine engine = GrepEngineFactory.GetSearchEngine(file, SearchParams);

						processedFiles++;

						Encoding encoding = null;
						if (codePage == -1)
							encoding = Utils.GetFileEncoding(file);
						else
							encoding = Encoding.GetEncoding(codePage);


						if (GrepCore.CancelProcess)
						{
							return searchResults;
						}

						List<GrepSearchResult> fileSearchResults = engine.Search(file, searchPattern, searchType, searchOptions, encoding);

						if (fileSearchResults != null)
						{
							searchResults.AddRange(fileSearchResults);
						}

						if (ProcessedFile != null)
						{
							if (PreviewFilesDuringSearch)
								ProcessedFile(this, new ProgressStatus(totalFiles, processedFiles, fileSearchResults));
							else
								ProcessedFile(this, new ProgressStatus(totalFiles, processedFiles, null));
						}

					}
					catch (Exception ex)
					{
						logger.LogException(LogLevel.Error, ex.Message, ex);
                        List<GrepSearchResult.GrepLine> lines = new List<GrepSearchResult.GrepLine>();
                        lines.Add(new GrepSearchResult.GrepLine(-1, ex.Message, false, null));
                        searchResults.Add(new GrepSearchResult(file, lines, false));
					}
				}
			}
			finally
			{
				GrepEngineFactory.UnloadEngines();
			}

			for (int i = 0; i < searchResults.Count; i++)
			{
				List<GrepSearchResult.GrepLine> lines = searchResults[i].SearchResults;
				//Utils.CleanResults(ref lines);
			}

			return searchResults;
		}

		public int Replace(string[] files, SearchType searchType, string baseFolder, string searchPattern, string replacePattern, GrepSearchOption searchOptions, int codePage)
		{
			string tempFolder = Utils.GetTempFolder();
			if (Directory.Exists(tempFolder))
				Utils.DeleteFolder(tempFolder);
			Directory.CreateDirectory(tempFolder);

			if (files == null || files.Length == 0 || !Directory.Exists(tempFolder) || !Directory.Exists(baseFolder))
				return 0;

			baseFolder = Utils.FixFolderName(baseFolder);
			tempFolder = Utils.FixFolderName(tempFolder);
            replacePattern = Utils.ReplaceSpecialCharacters(replacePattern);

			int totalFiles = files.Length;
			int processedFiles = 0;
			GrepCore.CancelProcess = false;

			try
			{
				foreach (string file in files)
				{
                    if (!file.Contains(baseFolder))
                        continue;

					string tempFileName = file.Replace(baseFolder, tempFolder);
					IGrepEngine engine = GrepEngineFactory.GetReplaceEngine(file, searchParams);

					try
					{
						processedFiles++;
						// Copy file					
						Utils.CopyFile(file, tempFileName, true);
						Utils.DeleteFile(file);

						Encoding encoding = null;
						if (codePage == -1)
							encoding = Utils.GetFileEncoding(tempFileName);
						else
							encoding = Encoding.GetEncoding(codePage);


						if (GrepCore.CancelProcess)
						{
							break;
						}

						if (!engine.Replace(tempFileName, file, searchPattern, replacePattern, searchType, searchOptions, encoding))
						{
							throw new ApplicationException("Replace failed for file: " + file);
						}

						if (!GrepCore.CancelProcess && ProcessedFile != null)
							ProcessedFile(this, new ProgressStatus(totalFiles, processedFiles, null));


						File.SetAttributes(file, File.GetAttributes(tempFileName));

						if (GrepCore.CancelProcess)
						{
							// Replace the file
							Utils.DeleteFile(file);
							Utils.CopyFile(tempFileName, file, true);
							break;
						}
					}
					catch (Exception ex)
					{
						logger.LogException(LogLevel.Error, ex.Message, ex);
						try
						{
							// Replace the file
							if (File.Exists(tempFileName) && File.Exists(file))
							{
								Utils.DeleteFile(file);
								Utils.CopyFile(tempFileName, file, true);
							}
						}
						catch (Exception ex2)
						{
							// DO NOTHING
						}
						return -1;
					}
				}
			}
			finally
			{
				GrepEngineFactory.UnloadEngines();
			}

			return processedFiles;
		}

		public bool Undo(string folderPath)
		{
			string tempFolder = Utils.GetTempFolder();
			if (!Directory.Exists(tempFolder))
			{
				logger.Error("Failed to undo replacement as temporary directory was removed.");
				return false;
			}
			try
			{
				Utils.CopyFiles(tempFolder, folderPath, null, null);
				return true;
			}
			catch (Exception ex)
			{
				logger.LogException(LogLevel.Error, "Failed to undo replacement", ex);
				return false;
			}
		}		
	}
}
