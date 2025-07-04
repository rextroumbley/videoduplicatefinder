// /*
//     Copyright (C) 2021 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GPLv3 as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//     VideoDuplicateFinder is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU General Public License for more details.
//     You should have received a copy of the GNU General Public License
//     along with VideoDuplicateFinder.  If not, see <http://www.gnu.org/licenses/>.
// */
//

global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Threading;
global using System.Threading.Tasks;
global using SixLabors.ImageSharp;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using SixLabors.ImageSharp.Processing;
using VDF.Core.FFTools;
using VDF.Core.Utils;
using VDF.Core.ViewModels;

namespace VDF.Core {

	public class SubClipMatch {
		public FileEntry MainVideo { get; set; }
		public FileEntry SubClipVideo { get; set; }
		public List<double> MainVideoMatchStartTimes { get; set; } = new List<double>();

		// Parameterless constructor for object initializers or deserialization
		public SubClipMatch() {
			MainVideo = null!; // Indicates to the compiler this will be initialized post-construction
			SubClipVideo = null!;
			// MainVideoMatchStartTimes is already initialized at declaration
		}

		// Parameterized constructor for explicit initialization
		public SubClipMatch(FileEntry mainVideo, FileEntry subClipVideo, List<double> mainVideoMatchStartTimes) {
			MainVideo = mainVideo;
			SubClipVideo = subClipVideo;
			MainVideoMatchStartTimes = mainVideoMatchStartTimes ?? new List<double>(); // Ensure list is not null
		}
	}

	public sealed class ScanEngine {
		public HashSet<DuplicateItem> Duplicates { get; set; } = new HashSet<DuplicateItem>();
		public Settings Settings { get; } = new Settings();
		public event EventHandler<ScanProgressChangedEventArgs>? Progress;
		public event EventHandler? BuildingHashesDone;
		public event EventHandler? ScanDone;
		public event EventHandler? ScanAborted;
		public event EventHandler? ThumbnailsRetrieved;
		public event EventHandler? FilesEnumerated;
		public event EventHandler? DatabaseCleaned;

		public Image? NoThumbnailImage;

		PauseTokenSource pauseTokenSource = new();
		CancellationTokenSource cancelationTokenSource = new();
		// readonly List<float> positionList = new(); // Removed

		bool isScanning;
		int scanProgressMaxValue;
		readonly Stopwatch SearchTimer = new();
		public Stopwatch ElapsedTimer = new();
		int processedFiles;
		DateTime startTime = DateTime.Now;
		DateTime lastProgressUpdate = DateTime.MinValue;
		static readonly TimeSpan progressUpdateIntervall = TimeSpan.FromMilliseconds(300);


		void InitProgress(int count) {
			startTime = DateTime.UtcNow;
			scanProgressMaxValue = count;
			processedFiles = 0;
			lastProgressUpdate = DateTime.MinValue;
		}
		void IncrementProgress(string path) {
			processedFiles++;
			var pushUpdate = processedFiles == scanProgressMaxValue ||
								lastProgressUpdate + progressUpdateIntervall < DateTime.UtcNow;
			if (!pushUpdate) return;
			lastProgressUpdate = DateTime.UtcNow;
			var timeRemaining = TimeSpan.FromTicks(DateTime.UtcNow.Subtract(startTime).Ticks *
									(scanProgressMaxValue - (processedFiles + 1)) / (processedFiles + 1));
			Progress?.Invoke(this,
							new ScanProgressChangedEventArgs {
								CurrentPosition = processedFiles,
								CurrentFile = path,
								Elapsed = ElapsedTimer.Elapsed,
								Remaining = timeRemaining,
								MaxPosition = scanProgressMaxValue
							});
		}

		public static bool FFmpegExists => !string.IsNullOrEmpty(FfmpegEngine.FFmpegPath);
		public static bool FFprobeExists => !string.IsNullOrEmpty(FFProbeEngine.FFprobePath);
		public static bool NativeFFmpegExists => FFTools.FFmpegNative.FFmpegHelper.DoFFmpegLibraryFilesExist;

		public async void StartSearch() {
			PrepareSearch();
			SearchTimer.Start();
			ElapsedTimer.Start();
			Logger.Instance.InsertSeparator('-');
			Logger.Instance.Info("Building file list...");
			await BuildFileList();
			Logger.Instance.Info($"Finished building file list in {SearchTimer.StopGetElapsedAndRestart()}");
			FilesEnumerated?.Invoke(this, new EventArgs());
			Logger.Instance.Info("Gathering media info and buildings hashes...");
			if (!cancelationTokenSource.IsCancellationRequested)
				await GatherInfos();
			Logger.Instance.Info($"Finished gathering and hashing in {SearchTimer.StopGetElapsedAndRestart()}");
			BuildingHashesDone?.Invoke(this, new EventArgs());
			DatabaseUtils.SaveDatabase();
			if (!cancelationTokenSource.IsCancellationRequested) {
				StartCompare();
			}
			else {
				ScanAborted?.Invoke(this, new EventArgs());
				Logger.Instance.Info("Scan aborted.");
				isScanning = false;
			}
		}

		public async void StartCompare() {
			PrepareCompare();
			SearchTimer.Start();
			ElapsedTimer.Start();
			Logger.Instance.Info("Scan for duplicates...");
			if (!cancelationTokenSource.IsCancellationRequested)
				await Task.Run(ScanForDuplicates, cancelationTokenSource.Token);
			SearchTimer.Stop();
			ElapsedTimer.Stop();
			Logger.Instance.Info($"Finished scanning for duplicates in {SearchTimer.Elapsed}");
			Logger.Instance.Info("Highlighting best results...");
			HighlightBestMatches();
			ScanDone?.Invoke(this, new EventArgs());
			Logger.Instance.Info("Scan done.");
			DatabaseUtils.SaveDatabase();
			isScanning = false;
		}

		void PrepareSearch() {
			//Using VDF.GUI we know fftools exist at this point but VDF.Core might be used in other projects as well
			if (!Settings.UseNativeFfmpegBinding && !FFmpegExists)
				throw new FFNotFoundException("Cannot find FFmpeg");
			if (!FFprobeExists)
				throw new FFNotFoundException("Cannot find FFprobe");
			if (Settings.UseNativeFfmpegBinding && !FFTools.FFmpegNative.FFmpegHelper.DoFFmpegLibraryFilesExist)
				throw new FFNotFoundException("Cannot find FFmpeg libraries");

			CancelAllTasks();

			FfmpegEngine.HardwareAccelerationMode = Settings.HardwareAccelerationMode;
			FfmpegEngine.CustomFFArguments = Settings.CustomFFArguments;
			FfmpegEngine.UseNativeBinding = Settings.UseNativeFfmpegBinding;
			DatabaseUtils.CustomDatabaseFolder = Settings.CustomDatabaseFolder;
			Duplicates.Clear();
			// positionList.Clear(); // Removed
			ElapsedTimer.Reset();
			SearchTimer.Reset();

			// Removed old positionList initialization

			isScanning = true;
		}

		void PrepareCompare() {
			// Old check based on ThumbnailCount and positionList.Count is removed.
			// A new check, if necessary for "quick rescan" integrity, would need to compare
			// the actual ThumbnailPositionSetting objects in Settings, which is more complex
			// and not specified in this subtask. For now, removing the check.

			CancelAllTasks();

			Duplicates.Clear();
			SearchTimer.Reset();
			if (!ElapsedTimer.IsRunning)
				ElapsedTimer.Reset();

			isScanning = true;
		}

		void CancelAllTasks() {
			if (!cancelationTokenSource.IsCancellationRequested)
				cancelationTokenSource.Cancel();
			cancelationTokenSource = new CancellationTokenSource();
			pauseTokenSource = new PauseTokenSource();
			isScanning = false;
		}

		Task BuildFileList() => Task.Run(() => {

			DatabaseUtils.LoadDatabase();
			int oldFileCount = DatabaseUtils.Database.Count;

			foreach (string path in Settings.IncludeList) {
				if (!Directory.Exists(path)) continue;

				foreach (FileInfo file in FileUtils.GetFilesRecursive(path, Settings.IgnoreReadOnlyFolders, Settings.IgnoreReparsePoints,
					Settings.IncludeSubDirectories, Settings.IncludeImages, Settings.BlackList.ToList())) {
					FileEntry fEntry;
					try {
						fEntry = new(file);
					}
					catch (Exception e) {
						//https://github.com/0x90d/videoduplicatefinder/issues/237
						Logger.Instance.Info($"Skipped file '{file}' because of {e}");
						continue;
					}
					if (!DatabaseUtils.Database.TryGetValue(fEntry, out var dbEntry))
						DatabaseUtils.Database.Add(fEntry);
					else if (fEntry.DateCreated != dbEntry.DateCreated ||
							fEntry.DateModified != dbEntry.DateModified ||
							fEntry.FileSize != dbEntry.FileSize) {
						// -> Modified or different file
						DatabaseUtils.Database.Remove(dbEntry);
						DatabaseUtils.Database.Add(fEntry);
					}
				}
			}

			Logger.Instance.Info($"Files in database: {DatabaseUtils.Database.Count:N0} ({DatabaseUtils.Database.Count - oldFileCount:N0} files added)");
		});

		// Check if entry should be excluded from the scan for any reason
		// Returns true if the entry is invalid (should be excluded)
		bool InvalidEntry(FileEntry entry, out bool reportProgress) {
			reportProgress = true;

			if (Settings.IncludeImages == false && entry.IsImage)
				return true;
			if (Settings.BlackList.Any(f => {
				if (!entry.Folder.StartsWith(f))
					return false;
				if (entry.Folder.Length == f.Length)
					return true;
				//Reason: https://github.com/0x90d/videoduplicatefinder/issues/249
				string relativePath = Path.GetRelativePath(f, entry.Folder);
				return !relativePath.StartsWith('.') && !Path.IsPathRooted(relativePath);
			}))
				return true;

			if (!Settings.ScanAgainstEntireDatabase) {
				/* Skip non-included file before checking if it exists
				 * This greatly improves performance if the file is on
				 * a disconnected network/mobile drive
				 */
				if (Settings.IncludeSubDirectories == false) {
					if (!Settings.IncludeList.Contains(entry.Folder)) {
						reportProgress = false;
						return true;
					}
				}
				else if (!Settings.IncludeList.Any(f => {
					if (!entry.Folder.StartsWith(f))
						return false;
					if (entry.Folder.Length == f.Length)
						return true;
					//Reason: https://github.com/0x90d/videoduplicatefinder/issues/249
					string relativePath = Path.GetRelativePath(f, entry.Folder);
					return !relativePath.StartsWith('.') && !Path.IsPathRooted(relativePath);
				})) {
					reportProgress = false;
					return true;
				}
			}

			if (entry.Flags.Any(EntryFlags.ManuallyExcluded | EntryFlags.TooDark))
				return true;
			if (!Settings.IncludeNonExistingFiles && !File.Exists(entry.Path))
				return true;

			if (Settings.FilterByFileSize && (entry.FileSize.BytesToMegaBytes() > Settings.MaximumFileSize ||
				entry.FileSize.BytesToMegaBytes() < Settings.MinimumFileSize)) {
				return true;
			}
			if (Settings.FilterByFilePathContains) {
				bool contains = false;
				foreach (var f in Settings.FilePathContainsTexts) {
					if (System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(f, entry.Path)) {
						contains = true;
						break;
					}
				}
				if (!contains)
					return true;
			}

			if (Settings.IgnoreReparsePoints && File.Exists(entry.Path) && File.ResolveLinkTarget(entry.Path, returnFinalTarget: false) != null)
				return true;
			if (Settings.FilterByFilePathNotContains) {
				bool contains = false;
				foreach (var f in Settings.FilePathNotContainsTexts) {
					if (System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(f, entry.Path)) {
						contains = true;
						break;
					}
				}
				if (contains)
					return true;
			}

			return false;
		}
		bool InvalidEntryForDuplicateCheck(FileEntry entry) =>
			entry.invalid || entry.mediaInfo == null || entry.Flags.Has(EntryFlags.ThumbnailError) || (!entry.IsImage && Settings.ThumbnailPositions.Any() && entry.grayBytes.Count < Settings.ThumbnailPositions.Count);

		public static Task<bool> LoadDatabase() => Task.Run(DatabaseUtils.LoadDatabase);
		public static void SaveDatabase() => DatabaseUtils.SaveDatabase();
		public static void RemoveFromDatabase(FileEntry dbEntry) => DatabaseUtils.Database.Remove(dbEntry);
		public static void UpdateFilePathInDatabase(string newPath, FileEntry dbEntry) => DatabaseUtils.UpdateFilePath(newPath, dbEntry);
#pragma warning disable CS8601 // Possible null reference assignment
		public static bool GetFromDatabase(string path, out FileEntry? dbEntry) {
			if (!File.Exists(path)) {
				dbEntry = null;
				return false;
			}
			return DatabaseUtils.Database.TryGetValue(new FileEntry(path), out dbEntry);
		}
#pragma warning restore CS8601 // Possible null reference assignment
		public static void BlackListFileEntry(string filePath) => DatabaseUtils.BlacklistFileEntry(filePath);

		async Task GatherInfos() {
			try {
				InitProgress(DatabaseUtils.Database.Count);
				await Parallel.ForEachAsync(DatabaseUtils.Database, new ParallelOptions { CancellationToken = cancelationTokenSource.Token, MaxDegreeOfParallelism = Settings.MaxDegreeOfParallelism }, (entry, token) => {
					while (pauseTokenSource.IsPaused) Thread.Sleep(50);

					entry.invalid = InvalidEntry(entry, out bool reportProgress);

					bool skipEntry = false;
					skipEntry |= entry.invalid;
					skipEntry |= entry.Flags.Has(EntryFlags.ThumbnailError) && !Settings.AlwaysRetryFailedSampling;

					if (!skipEntry && !Settings.ScanAgainstEntireDatabase) {
						if (Settings.IncludeSubDirectories == false) {
							if (!Settings.IncludeList.Contains(entry.Folder))
								skipEntry = true;
						}
						else if (!Settings.IncludeList.Any(f => {
							if (!entry.Folder.StartsWith(f))
								return false;
							if (entry.Folder.Length == f.Length)
								return true;
							//Reason: https://github.com/0x90d/videoduplicatefinder/issues/249
							string relativePath = Path.GetRelativePath(f, entry.Folder);
							return !relativePath.StartsWith('.') && !Path.IsPathRooted(relativePath);
						}))
							skipEntry = true;
					}

					if (skipEntry) {
						entry.invalid = true;
						if (reportProgress)
							IncrementProgress(entry.Path);
						return ValueTask.CompletedTask;
					}
					if (Settings.IncludeNonExistingFiles && entry.grayBytes?.Count > 0) {
						bool hasAllInformation = entry.IsImage;
						if (!hasAllInformation && entry.mediaInfo != null && entry.mediaInfo.Duration.TotalSeconds > 0) {
							hasAllInformation = true; // Assume true, then check
                            if (Settings.ThumbnailPositions.Any()) {
                                foreach (var posSetting in Settings.ThumbnailPositions) {
                                    double expectedKey = CalculateExpectedGrayBytesKey(posSetting, entry.mediaInfo.Duration);
                                    if (!entry.grayBytes.ContainsKey(expectedKey) || entry.grayBytes[expectedKey] == null) {
                                        hasAllInformation = false;
                                        break;
                                    }
                                }
                            } else { // No thumbnail positions defined, so technically all (zero) required thumbnails are present.
                                hasAllInformation = true;
                            }
						}
						bool oldGrayBytesExisted = entry.grayBytes?.Count > 0; // Check before potential clear
						if (!hasAllInformation && oldGrayBytesExisted) {
							Logger.Instance.Info($"INFO: Clearing inconsistent or incomplete thumbnails for {entry.Path} based on hasAllInformation check.");
							entry.grayBytes.Clear();
						}
						if (hasAllInformation) {
							IncrementProgress(entry.Path);
							return ValueTask.CompletedTask;
						}
					}

					// Ensure grayBytes is initialized (might be redundant if already done, but safe)
					entry.grayBytes ??= new Dictionary<double, byte[]?>();

					if (entry.mediaInfo == null && !entry.IsImage) {
						MediaInfo? info = FFProbeEngine.GetMediaInfo(entry.Path, Settings.ExtendedFFToolsLogging);
						if (info == null) {
							entry.invalid = true;
							entry.Flags.Set(EntryFlags.MetadataError);
							IncrementProgress(entry.Path);
							return ValueTask.CompletedTask;
						}

						entry.mediaInfo = info;
					}
					//Moved earlier: entry.grayBytes ??= new Dictionary<double, byte[]?>();


					if (entry.IsImage && entry.grayBytes.Count == 0) {
						if (!GetGrayBytesFromImage(entry))
							entry.invalid = true;
					}
					else if (!entry.IsImage) { // This block is for videos
                        // Clear grayBytes if settings define no positions but old thumbnails exist
                        if (!Settings.ThumbnailPositions.Any() && entry.grayBytes.Count > 0) {
                            Logger.Instance.Info($"INFO: Clearing thumbnails for {entry.Path} as no thumbnail positions are currently defined.");
                            entry.grayBytes.Clear();
                        }

                        List<float> positionListForThisVideo = new List<float>();
                        if (entry.mediaInfo != null && entry.mediaInfo.Duration.TotalSeconds > 0) {
                            foreach (var posSetting in Settings.ThumbnailPositions) {
                                float percentage = 0f;
                                switch (posSetting.Type) {
                                    case ThumbnailPositionSetting.PositionType.Percentage:
                                        percentage = (float)(posSetting.Value / 100.0);
                                        break;
                                    case ThumbnailPositionSetting.PositionType.OffsetFromStart:
                                        percentage = entry.mediaInfo.Duration.TotalSeconds > 0 ? (float)(posSetting.Value / entry.mediaInfo.Duration.TotalSeconds) : 0f;
                                        break;
                                    case ThumbnailPositionSetting.PositionType.OffsetFromEnd:
                                        double timeFromStart = entry.mediaInfo.Duration.TotalSeconds - posSetting.Value;
                                        percentage = entry.mediaInfo.Duration.TotalSeconds > 0 ? (float)(timeFromStart / entry.mediaInfo.Duration.TotalSeconds) : 0f;
                                        break;
                                }
                                positionListForThisVideo.Add(Math.Clamp(percentage, 0.0f, 1.0f));
                            }
                        }

                        // Determine if extraction is needed
                        bool needsExtraction = Settings.ThumbnailPositions.Any() &&
                                               entry.grayBytes.Count < Settings.ThumbnailPositions.Count &&
                                               (entry.mediaInfo?.Duration.TotalSeconds ?? 0) > 0;


                        if (Settings.AlwaysRetryFailedSampling && entry.Flags.Has(EntryFlags.ThumbnailError)) {
                            if (!needsExtraction && entry.grayBytes.Count > 0 && Settings.ThumbnailPositions.Any()) {
                                // If retry is forced, but counts seemed to match, still clear to ensure re-extraction.
                                Logger.Instance.Info($"INFO: Retrying failed thumbnail sampling for {entry.Path}. Clearing existing thumbnails.");
                                entry.grayBytes.Clear();
                            }
                            needsExtraction = Settings.ThumbnailPositions.Any() && (entry.mediaInfo?.Duration.TotalSeconds ?? 0) > 0; // Re-evaluate after clearing
                            entry.Flags &= ~EntryFlags.ThumbnailError; // Correct way to remove a flag
                        }

                        if (needsExtraction && positionListForThisVideo.Any()) {
						    if (!FfmpegEngine.GetGrayBytesFromVideo(entry, positionListForThisVideo, Settings.ExtendedFFToolsLogging)) {
							    entry.invalid = true;
                                entry.Flags.Set(EntryFlags.ThumbnailError); // Explicitly set error flag on failure
                            }
                        } else if (needsExtraction && !positionListForThisVideo.Any() && Settings.ThumbnailPositions.Any()) {
                             // This case implies settings expect thumbnails, but we couldn't generate positions (e.g. zero duration video for offset math).
                             // Mark as thumbnail error if media info is present, otherwise metadata error might already be set.
                             if(entry.mediaInfo != null) entry.Flags.Set(EntryFlags.ThumbnailError);
                             Logger.Instance.Info($"WARNING: Could not generate thumbnail positions for {entry.Path} (Duration: {entry.mediaInfo?.Duration.TotalSeconds}s), marking as thumbnail error.");
                        }
					}

					IncrementProgress(entry.Path);
					return ValueTask.CompletedTask;
				});
			}
			catch (OperationCanceledException) { }
		}

		Dictionary<double, byte[]?> CreateFlippedGrayBytes(FileEntry entry) {
			Dictionary<double, byte[]?>? flippedGrayBytes = new();
			if (entry.IsImage) {
				if (entry.grayBytes.TryGetValue(0, out var tb) && tb != null)
					flippedGrayBytes.Add(0, GrayBytesUtils.FlipGrayScale(tb));
			} else {
                if (entry.mediaInfo != null && entry.mediaInfo.Duration.TotalSeconds > 0 && Settings.ThumbnailPositions.Any()) {
                    foreach (var posSetting in Settings.ThumbnailPositions) {
                        double keyEntry = CalculateExpectedGrayBytesKey(posSetting, entry.mediaInfo.Duration);
                        if (entry.grayBytes.TryGetValue(keyEntry, out byte[]? entryTb) && entryTb != null) {
                            flippedGrayBytes.Add(keyEntry, GrayBytesUtils.FlipGrayScale(entryTb));
                        }
                    }
                }
			}
			return flippedGrayBytes;
		}
        private double CalculateExpectedGrayBytesKey(ThumbnailPositionSetting posSetting, TimeSpan duration) {
            float percentage = 0f;
            switch (posSetting.Type) {
                case ThumbnailPositionSetting.PositionType.Percentage:
                    percentage = (float)(posSetting.Value / 100.0);
                    break;
                case ThumbnailPositionSetting.PositionType.OffsetFromStart:
                    if (duration.TotalSeconds == 0) percentage = 0;
                    else percentage = (float)(posSetting.Value / duration.TotalSeconds);
                    break;
                case ThumbnailPositionSetting.PositionType.OffsetFromEnd:
                    if (duration.TotalSeconds == 0) percentage = 0;
                    else {
                        double timeFromStart = duration.TotalSeconds - posSetting.Value;
                        percentage = (float)(timeFromStart / duration.TotalSeconds);
                    }
                    break;
            }
            percentage = Math.Clamp(percentage, 0.0f, 1.0f);
            return duration.TotalSeconds * percentage;
        }

		bool CheckIfDuplicate(FileEntry entry, Dictionary<double, byte[]?>? grayBytesToCompare, FileEntry compItem, out float difference) {
			grayBytesToCompare ??= entry.grayBytes;
			bool ignoreBlackPixels = Settings.IgnoreBlackPixels;
			bool ignoreWhitePixels = Settings.IgnoreWhitePixels;
			float differenceLimit = 1.0f - Settings.Percent / 100f;
			difference = 1f;

			if (entry.IsImage) {
                if (grayBytesToCompare.TryGetValue(0, out byte[]? entryTb) && entryTb != null &&
                    compItem.grayBytes.TryGetValue(0, out byte[]? compTb) && compTb != null) {
                    difference = ignoreBlackPixels || ignoreWhitePixels ?
                                    GrayBytesUtils.PercentageDifferenceWithoutSpecificPixels(entryTb, compTb, ignoreBlackPixels, ignoreWhitePixels) :
                                    GrayBytesUtils.PercentageDifference(entryTb, compTb);
                    return difference <= differenceLimit;
                }
                return false;
			}

            if (Settings.ThumbnailPositions.Count == 0) {
                return false;
            }

            float diffSum = 0;
            int validComparisons = 0;

            foreach (var posSetting in Settings.ThumbnailPositions) {
                if (entry.mediaInfo == null || compItem.mediaInfo == null ||
                    entry.mediaInfo.Duration.TotalSeconds == 0 || compItem.mediaInfo.Duration.TotalSeconds == 0) {
                    continue;
                }

                double keyEntry = CalculateExpectedGrayBytesKey(posSetting, entry.mediaInfo.Duration);
                double keyCompItem = CalculateExpectedGrayBytesKey(posSetting, compItem.mediaInfo.Duration);

                if (grayBytesToCompare.TryGetValue(keyEntry, out byte[]? entryTb) && entryTb != null &&
                    compItem.grayBytes.TryGetValue(keyCompItem, out byte[]? compTb) && compTb != null) {

                    float singleDiff = ignoreBlackPixels || ignoreWhitePixels ?
                                GrayBytesUtils.PercentageDifferenceWithoutSpecificPixels(entryTb, compTb, ignoreBlackPixels, ignoreWhitePixels) :
                                GrayBytesUtils.PercentageDifference(entryTb, compTb);

                    if (singleDiff > differenceLimit) {
                        difference = singleDiff;
                        return false;
                    }
                    diffSum += singleDiff;
                    validComparisons++;
                } else {
                     difference = 1f;
                     return false;
                }
            }

            if (validComparisons == 0) {
                return false;
            }

            difference = diffSum / validComparisons;
            return !float.IsNaN(difference);
		}

		void ScanForDuplicates() {
			DateTime cutoffDateTime = DateTime.MinValue;
			Dictionary<string, DuplicateItem>? duplicateDict = new();

			//Exclude existing database entries which not met current scan settings
			List<FileEntry> ScanList = new();

			Logger.Instance.Info("Prepare list of items to compare...");
			foreach (FileEntry entry in DatabaseUtils.Database) {
				if (!InvalidEntryForDuplicateCheck(entry)) {
					ScanList.Add(entry);
				}
			}

			Logger.Instance.Info($"Scanning for duplicates in {ScanList.Count:N0} files");

			InitProgress(ScanList.Count);

			if (Settings.EnableTimeLimitedScan) {
				cutoffDateTime = DateTime.UtcNow - TimeSpan.FromSeconds(Settings.TimeLimitSeconds);
			}

			double maxPercentDurationDifference = 100d + Settings.PercentDurationDifference;
			double minPercentDurationDifference = 100d - Settings.PercentDurationDifference;

			try {
				Parallel.For(0, ScanList.Count, new ParallelOptions { CancellationToken = cancelationTokenSource.Token, MaxDegreeOfParallelism = Settings.MaxDegreeOfParallelism }, i => {
					while (pauseTokenSource.IsPaused) Thread.Sleep(50);

					FileEntry? entry = ScanList[i];

					if (Settings.EnableTimeLimitedScan && entry.DateModified < cutoffDateTime) {
						IncrementProgress(entry.Path); // Still need to report progress for skipped items
						return; // Using return instead of continue because it's a Parallel.For body
					}

					float difference = 0;
					DuplicateFlags flags = DuplicateFlags.None;
					bool isDuplicate;
					Dictionary<double, byte[]?>? flippedGrayBytes = null;

					if (Settings.CompareHorizontallyFlipped)
						flippedGrayBytes = CreateFlippedGrayBytes(entry);

					for (int n = i + 1; n < ScanList.Count; n++) {
						FileEntry? compItem = ScanList[n];

						if (Settings.EnableTimeLimitedScan && compItem.DateModified < cutoffDateTime) {
							continue;
						}

						if (entry.IsImage != compItem.IsImage)
							continue;
						if (!entry.IsImage) {
							double p = entry.mediaInfo!.Duration.TotalSeconds / compItem.mediaInfo!.Duration.TotalSeconds * 100d;
							if (p > maxPercentDurationDifference ||
								p < minPercentDurationDifference)
								continue;
						}


						flags = DuplicateFlags.None;
						isDuplicate = CheckIfDuplicate(entry, null, compItem, out difference);
						if (Settings.CompareHorizontallyFlipped &&
							CheckIfDuplicate(entry, flippedGrayBytes, compItem, out float flippedDifference)) {
							if (!isDuplicate || flippedDifference < difference) {
								flags |= DuplicateFlags.Flipped;
								isDuplicate = true;
								difference = flippedDifference;
							}
						}

						if (isDuplicate &&
							entry.FileSize == compItem.FileSize &&
							entry.mediaInfo!.Duration == compItem.mediaInfo!.Duration &&
							Settings.ExcludeHardLinks) {
							foreach (var link in HardLinkUtils.GetHardLinks(entry.Path))
								if (compItem.Path == link) {
									isDuplicate = false;
									break;
								}
						}

						if (isDuplicate) {
							lock (duplicateDict) {
								bool foundBase = duplicateDict.TryGetValue(entry.Path, out DuplicateItem? existingBase);
								bool foundComp = duplicateDict.TryGetValue(compItem.Path, out DuplicateItem? existingComp);

								if (foundBase && foundComp) {
									//this happens with 4+ identical items:
									//first, 2+ duplicate groups are found independently, they are merged in this branch
									if (existingBase!.GroupId != existingComp!.GroupId) {
										Guid groupID = existingComp!.GroupId;
										foreach (DuplicateItem? dup in duplicateDict.Values.Where(c =>
											c.GroupId == groupID))
											dup.GroupId = existingBase.GroupId;
									}
								}
								else if (foundBase) {
									duplicateDict.TryAdd(compItem.Path,
										new DuplicateItem(compItem, difference, existingBase!.GroupId, flags));
								}
								else if (foundComp) {
									duplicateDict.TryAdd(entry.Path,
										new DuplicateItem(entry, difference, existingComp!.GroupId, flags));
								}
								else {
									var groupId = Guid.NewGuid();
									duplicateDict.TryAdd(compItem.Path, new DuplicateItem(compItem, difference, groupId, flags));
									duplicateDict.TryAdd(entry.Path, new DuplicateItem(entry, difference, groupId, DuplicateFlags.None));
								}
							}
						}
					}
					IncrementProgress(entry.Path);
				});
			}
			catch (OperationCanceledException) { }
			Duplicates = new HashSet<DuplicateItem>(duplicateDict.Values);
		}
		public async void CleanupDatabase() {
			await Task.Run(() => {
				DatabaseUtils.CleanupDatabase();
			});
			DatabaseCleaned?.Invoke(this, new EventArgs());
		}
		public static void ClearDatabase() => DatabaseUtils.ClearDatabase();
		public static bool ExportDataBaseToJson(string jsonFile, JsonSerializerOptions options) => DatabaseUtils.ExportDatabaseToJson(jsonFile, options);
		public static bool ImportDataBaseFromJson(string jsonFile, JsonSerializerOptions options) => DatabaseUtils.ImportDatabaseFromJson(jsonFile, options);
		public async void RetrieveThumbnails() {
			var dupList = Duplicates.Where(d => d.ImageList == null || d.ImageList.Count == 0).ToList();
			try {
				await Parallel.ForEachAsync(dupList, new ParallelOptions { CancellationToken = cancelationTokenSource.Token, MaxDegreeOfParallelism = Settings.MaxDegreeOfParallelism }, (entry, cancellationToken) => {
					// 'entry' here is a DuplicateItem
					FileEntry? fileEntryFromDb = null;
					// DatabaseUtils.Database is a HashSet, which is not ideal for direct lookup by path without creating a new FileEntry for comparison.
					// A potentially more performant way if DatabaseUtils.Database is large and this is frequent:
					// First, ensure DatabaseUtils.GetFromDatabase can be used or adapt its usage.
					// For now, let's use a LINQ FirstOrDefault, assuming Database is accessible.
					// This requires DatabaseUtils.Database to be accessible here.
					// If ScanEngine has a copy or direct access to the HashSet<FileEntry> used in scanning (e.g. a filtered list like ScanList), that could be used too.
					// Let's assume DatabaseUtils.Database is the source of truth for FileEntries.
					if (File.Exists(entry.Path)) { // Check if file exists before trying to get from DB
						// DatabaseUtils.GetFromDatabase(entry.Path, out fileEntryFromDb); // Incorrect call
						var keyFileEntry = new FileEntry(entry.Path); // Create a FileEntry key for lookup
						DatabaseUtils.Database.TryGetValue(keyFileEntry, out fileEntryFromDb);
					}

					if (fileEntryFromDb == null || (!entry.IsImage && fileEntryFromDb.mediaInfo == null)) { // For videos, mediaInfo is essential
						// Cannot retrieve thumbnails if FileEntry or its mediaInfo is missing for videos.
						// For images, mediaInfo might not be strictly necessary for basic thumbnail retrieval if path is known.
						Logger.Instance.Info($"WARNING: Could not find FileEntry or mediaInfo for {entry.Path} in RetrieveThumbnails. Skipping thumbnail retrieval for this item.");
						entry.SetThumbnails(new List<Image>(), new List<TimeSpan>()); // Set empty lists
						return ValueTask.CompletedTask; // Continue to next item in Parallel.ForEachAsync
					}

					List<Image>? list = null;
					bool needsThumbnails = !Settings.IncludeNonExistingFiles || File.Exists(entry.Path);
					List<TimeSpan>? timeStamps = null;

					if (needsThumbnails && entry.IsImage) {
						//For images it doesn't make sense to load the actual image more than once
						timeStamps = new List<TimeSpan> { TimeSpan.Zero }; // Image is at time 0
						list = new List<Image>(1);
						try {
							Image bitmapImage = Image.Load(entry.Path);
							float resizeFactor = 1f;
							if (bitmapImage.Width > 100 || bitmapImage.Height > 100) {
								float widthFactor = bitmapImage.Width / 100f;
								float heightFactor = bitmapImage.Height / 100f;
								resizeFactor = Math.Max(widthFactor, heightFactor);
							}
							int width = Convert.ToInt32(bitmapImage.Width / resizeFactor);
							int height = Convert.ToInt32(bitmapImage.Height / resizeFactor);
							bitmapImage.Mutate(i => i.Resize(width, height));
							list.Add(bitmapImage);
						}
						catch (Exception ex) {
							Logger.Instance.Info($"WARNING: Failed loading image from file: '{entry.Path}', reason: {ex.Message}, stacktrace {ex.StackTrace}");
							// Add placeholder if loading fails
                            list.Add(NoThumbnailImage ?? new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(1,1));
						}
					}
					// Use fileEntryFromDb.mediaInfo for videos
					else if (needsThumbnails && fileEntryFromDb.mediaInfo != null) { // Video processing
                        if (fileEntryFromDb.mediaInfo.Duration.TotalSeconds > 0 && Settings.ThumbnailPositions.Any()) {
                            list = new List<Image>(Settings.ThumbnailPositions.Count);
                            timeStamps = new List<TimeSpan>(Settings.ThumbnailPositions.Count);

                            foreach(var posSetting in Settings.ThumbnailPositions) {
                                // Use fileEntryFromDb.mediaInfo.Duration
                                TimeSpan actualTimestamp = TimeSpan.FromSeconds(CalculateExpectedGrayBytesKey(posSetting, fileEntryFromDb.mediaInfo.Duration));
                                timeStamps.Add(actualTimestamp);

                                var b = FfmpegEngine.GetThumbnail(new FfmpegSettings {
                                    File = entry.Path, // entry.Path is fine here as DuplicateItem has Path
                                    Position = actualTimestamp,
                                    GrayScale = 0, // Color thumbnail for preview
                                }, Settings.ExtendedFFToolsLogging);

                                if (b == null || b.Length == 0) {
                                    list.Add(NoThumbnailImage ?? new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(1,1));
                                    continue;
                                }
                                try {
                                    using var byteStream = new MemoryStream(b);
                                    var bitmapImage = Image.Load(byteStream);
                                    list.Add(bitmapImage);
                                } catch (Exception ex) {
                                    Logger.Instance.Info($"WARNING: Failed to load thumbnail image from byte stream for {entry.Path} at {actualTimestamp}. Exception: {ex.Message}");
                                    list.Add(NoThumbnailImage ?? new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(1,1));
                                }
                            }
                        } else {
                            list = new List<Image>();
                            timeStamps = new List<TimeSpan>();
                        }
					}
                    timeStamps ??= new List<TimeSpan>();
					entry.SetThumbnails(list ?? (NoThumbnailImage != null ? new() { NoThumbnailImage } : new()), timeStamps);
					return ValueTask.CompletedTask;
				});
			}
			catch (OperationCanceledException) { }
			ThumbnailsRetrieved?.Invoke(this, new EventArgs());
		}

		static bool GetGrayBytesFromImage(FileEntry imageFile) {
			try {

				using var byteStream = File.OpenRead(imageFile.Path);
				using var bitmapImage = Image.Load(byteStream);
				//Set some props while we already loaded the image
				imageFile.mediaInfo = new MediaInfo {
					Streams = new[] {
							new MediaInfo.StreamInfo {Height = bitmapImage.Height, Width = bitmapImage.Width}
						}
				};
				bitmapImage.Mutate(a => a.Resize(16, 16));

				var d = GrayBytesUtils.GetGrayScaleValues(bitmapImage);
				if (d == null) {
					imageFile.Flags.Set(EntryFlags.TooDark);
					Logger.Instance.Info($"ERROR: Graybytes too dark of: {imageFile.Path}");
					return false;
				}

				imageFile.grayBytes.Add(0, d);
				return true;
			}
			catch (Exception ex) {
				Logger.Instance.Info(
					$"Exception, file: {imageFile.Path}, reason: {ex.Message}, stacktrace {ex.StackTrace}");
				imageFile.Flags.Set(EntryFlags.ThumbnailError);
				return false;
			}
		}

		void HighlightBestMatches() {
			HashSet<Guid> blackList = new();
			foreach (DuplicateItem item in Duplicates) {
				if (blackList.Contains(item.GroupId)) continue;
				var groupItems = Duplicates.Where(a => a.GroupId == item.GroupId);
				DuplicateItem bestMatch;
				//Duration
				if (!groupItems.First().IsImage) {
					groupItems = groupItems.OrderByDescending(d => d.Duration);
					bestMatch = groupItems.First();
					bestMatch.IsBestDuration = true;
					foreach (DuplicateItem otherItem in groupItems.Skip(1)) {
						if (otherItem.Duration < bestMatch.Duration)
							break;
						otherItem.IsBestDuration = true;
					}
				}
				//Size
				groupItems = groupItems.OrderBy(d => d.SizeLong);
				bestMatch = groupItems.First();
				bestMatch.IsBestSize = true;
				foreach (DuplicateItem otherItem in groupItems.Skip(1)) {
					if (otherItem.SizeLong > bestMatch.SizeLong)
						break;
					otherItem.IsBestSize = true;
				}
				//Fps
				if (!groupItems.First().IsImage) {
					groupItems = groupItems.OrderByDescending(d => d.Fps);
					bestMatch = groupItems.First();
					bestMatch.IsBestFps = true;
					foreach (DuplicateItem otherItem in groupItems.Skip(1)) {
						if (otherItem.Fps < bestMatch.Fps)
							break;
						otherItem.IsBestFps = true;
					}
				}
				//BitRateKbs
				if (!groupItems.First().IsImage) {
					groupItems = groupItems.OrderByDescending(d => d.BitRateKbs);
					bestMatch = groupItems.First();
					bestMatch.IsBestBitRateKbs = true;
					foreach (DuplicateItem otherItem in groupItems.Skip(1)) {
						if (otherItem.BitRateKbs < bestMatch.BitRateKbs)
							break;
						otherItem.IsBestBitRateKbs = true;
					}
				}
				//AudioSampleRate
				if (!groupItems.First().IsImage) {
					groupItems = groupItems.OrderByDescending(d => d.AudioSampleRate);
					bestMatch = groupItems.First();
					bestMatch.IsBestAudioSampleRate = true;
					foreach (DuplicateItem otherItem in groupItems.Skip(1)) {
						if (otherItem.AudioSampleRate < bestMatch.AudioSampleRate)
							break;
						otherItem.IsBestAudioSampleRate = true;
					}
				}
				//FrameSizeInt
				groupItems = groupItems.OrderByDescending(d => d.FrameSizeInt);
				bestMatch = groupItems.First();
				bestMatch.IsBestFrameSize = true;
				foreach (DuplicateItem otherItem in groupItems.Skip(1)) {
					if (otherItem.FrameSizeInt < bestMatch.FrameSizeInt)
						break;
					otherItem.IsBestFrameSize = true;
				}
				blackList.Add(item.GroupId);
			}
		}

		public void Pause() {
			if (!isScanning || pauseTokenSource.IsPaused) return;
			Logger.Instance.Info("Scan paused by user");
			ElapsedTimer.Stop();
			SearchTimer.Stop();
			pauseTokenSource.IsPaused = true;

		}

		public void Resume() {
			if (!isScanning || pauseTokenSource.IsPaused != true) return;
			Logger.Instance.Info("Scan resumed by user");
			ElapsedTimer.Start();
			SearchTimer.Start();
			pauseTokenSource.IsPaused = false;
		}

		public void Stop() {
			if (pauseTokenSource.IsPaused)
				Resume();
			Logger.Instance.Info("Scan stopped by user");
			if (isScanning)
				cancelationTokenSource.Cancel();
		}

		public List<FileEntry> GetBrokenFileEntries() {
			List<FileEntry> brokenEntries = new List<FileEntry>();
			if (DatabaseUtils.Database == null) {
				return brokenEntries;
			}
			foreach (FileEntry entry in DatabaseUtils.Database) {
				if (entry.Flags.HasFlag(EntryFlags.MetadataError) || entry.Flags.HasFlag(EntryFlags.ThumbnailError)) {
					brokenEntries.Add(entry);
				}
			}
			return brokenEntries;
		}

		public List<SubClipMatch> FindSubClipMatches(IEnumerable<FileEntry> allFiles, Settings settings) {
			List<SubClipMatch> matches = new List<SubClipMatch>();
			var fileList = allFiles.ToList(); // Convert to list for easier handling if needed, or just use as is.

			foreach (FileEntry potentialMain in fileList) {
				foreach (FileEntry potentialSub in fileList) {
					if (potentialMain == potentialSub) continue;
					if (potentialMain.mediaInfo == null || potentialSub.mediaInfo == null) continue;
					if (potentialMain.IsImage || potentialSub.IsImage) continue;
					if (potentialMain.mediaInfo.Duration <= potentialSub.mediaInfo.Duration) continue;
					if (potentialMain.grayBytes == null || potentialSub.grayBytes == null) continue;

                                        // Ensure enough thumbnails based on current settings.
                                        // The original logic in ScanForDuplicates uses positionList.Count which is derived from Settings.ThumbnailCount
                                        // So, we use settings.ThumbnailPositions.Count directly here for clarity for sub-clip detection.
					if (potentialMain.grayBytes.Count < settings.ThumbnailPositions.Count || potentialSub.grayBytes.Count < settings.ThumbnailPositions.Count) continue;

					var mainThumbnails = potentialMain.grayBytes.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value).ToList();
					var subThumbnails = potentialSub.grayBytes.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value).ToList();
					var mainThumbnailTimes = potentialMain.grayBytes.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Key).ToList();

					if (mainThumbnails.Count < subThumbnails.Count) continue;
					if (subThumbnails.Count == 0) continue; // Cannot match if sub-clip has no thumbnails

					float differenceLimit = 1.0f - settings.Percent / 100f;

					for (int i = 0; i <= mainThumbnails.Count - subThumbnails.Count; i++) {
						bool currentWindowMatch = true;
						List<double> currentMatchTimes = new List<double>();
						for (int j = 0; j < subThumbnails.Count; j++) {
							byte[]? mainTb = mainThumbnails[i + j];
							byte[]? subTb = subThumbnails[j];

							if (mainTb == null || subTb == null) {
								currentWindowMatch = false;
								break;
							}

                                                        // Using the same logic as CheckIfDuplicate for consistency, including ignoring specific pixels
                                                        float difference;
                                                        if (settings.IgnoreBlackPixels || settings.IgnoreWhitePixels) {
                                                            difference = GrayBytesUtils.PercentageDifferenceWithoutSpecificPixels(mainTb, subTb, settings.IgnoreBlackPixels, settings.IgnoreWhitePixels);
                                                        } else {
                                                            difference = GrayBytesUtils.PercentageDifference(mainTb, subTb);
                                                        }

							if (difference > differenceLimit) {
								currentWindowMatch = false;
								break;
							}
							currentMatchTimes.Add(mainThumbnailTimes[i + j]);
						}

						if (currentWindowMatch) {
							// Check if this exact match (main, sub, and specific start times) already exists
							// This is a simple check; more sophisticated grouping might be needed later
							bool alreadyExists = matches.Any(m => m.MainVideo == potentialMain &&
							                                  m.SubClipVideo == potentialSub &&
							                                  m.MainVideoMatchStartTimes.SequenceEqual(currentMatchTimes));
							if (!alreadyExists) {
								matches.Add(new SubClipMatch {
									MainVideo = potentialMain,
									SubClipVideo = potentialSub,
									MainVideoMatchStartTimes = new List<double>(currentMatchTimes) // Ensure a new list is added
								});
							}
							// Depending on desired behavior, one might want to `break;` here
							// if only the first sequence match per (main, sub) pair is needed.
							// For now, collect all distinct sequence matches.
						}
					}
				}
			}
			return matches;
		}
	}
}

