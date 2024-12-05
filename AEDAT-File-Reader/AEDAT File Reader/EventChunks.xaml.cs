﻿using AEDAT_File_Reader.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=234238

namespace AEDAT_File_Reader
{
	/// <summary>
	/// An empty page that can be used on its own or navigated to within a Frame.
	/// </summary>
	public sealed partial class EventChunks : Page
	{
		public EventChunks()
		{

			this.InitializeComponent();
		}

		readonly ContentDialog videoExportCompleteDialog = new ContentDialog()
		{
			Title = "Done",
			Content = "Video export complete",
			CloseButtonText = "Close"
		};
		readonly ContentDialog invaldVideoSettingsDialog = new ContentDialog()
		{
			Title = "Invalid Input",
			Content = "One or more video settings are invalid.",
			CloseButtonText = "Close"
		};
		readonly ContentDialog invalidCameraDataDialog = new ContentDialog()
		{
			Title = "Error",
			Content = "Could not parse camera parameters.",
			CloseButtonText = "Close"
		};

		string previousValueMaxFrame = "100";
		string previousValueTimePerFrame = "1000";


		private async void SelectFile_Tapped(object sender, TappedRoutedEventArgs e)
		{
			int frameTime;
			int maxFrames;
			try
			{
				// Grab video reconstruction settings from GUI
				// Will throw a FormatException if input is invalid (negative numbers or input has letters)
				(frameTime, maxFrames) = ParseVideoSettings();
			}
			catch (FormatException)
			{
				await invaldVideoSettingsDialog.ShowAsync();
				return;
			}

			// Select AEDAT file to be converted to video
			var picker = new FileOpenPicker
			{
				ViewMode = PickerViewMode.Thumbnail,
				SuggestedStartLocation = PickerLocationId.PicturesLibrary
			};
			picker.FileTypeFilter.Add(".AEDAT");


			// Select AEDAT file to be converted
			IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();

			if (files == null) {
                showLoading.IsActive = false;
                backgroundTint.Visibility = Windows.UI.Xaml.Visibility.Collapsed;
                return;
            }


			var picker2 = new FolderPicker
			{
				ViewMode = PickerViewMode.Thumbnail,
				SuggestedStartLocation = PickerLocationId.PicturesLibrary
			};
			picker2.FileTypeFilter.Add("*");
			// Select AEDAT file to be converted
			StorageFolder folder = await picker2.PickSingleFolderAsync();
			if (folder == null)
			{
				showLoading.IsActive = false;
				backgroundTint.Visibility = Windows.UI.Xaml.Visibility.Collapsed;
				return;
			}


			foreach(StorageFile file in files)
			{
				byte[] aedatFile = await AedatUtilities.ReadToBytes(file);

				// Determine camera type from AEDAT header
				string cameraTypeSearch = AedatUtilities.FindLineInHeader(AedatUtilities.hardwareInterfaceCheck, ref aedatFile);
				CameraParameters cam = AedatUtilities.ParseCameraModel(cameraTypeSearch);
				if (cam == null)
				{
					await invalidCameraDataDialog.ShowAsync();
					return;
				}
				showLoading.IsActive = true;
				backgroundTint.Visibility = Windows.UI.Xaml.Visibility.Visible;

				StorageFolder folder2 = await folder.CreateFolderAsync(file.Name.Replace(".aedat", "") + " Event Chunks");

				if (playbackType.IsOn)
				{
					await TimeBasedReconstruction(aedatFile, cam, frameTime, maxFrames, folder2, file.Name.Replace(".aedat", ""));
				}
				else
				{
					int numOfEvents = Int32.Parse(numOfEventInput.Text);
					await EventBasedReconstruction(aedatFile, cam, numOfEvents, maxFrames, folder2, file.Name.Replace(".aedat", ""));
				}
			}
			showLoading.IsActive = false;
			backgroundTint.Visibility = Windows.UI.Xaml.Visibility.Collapsed;
		}



		public async Task TimeBasedReconstruction(byte[] aedatFile, CameraParameters cam,  int frameTime, int maxFrames, StorageFolder folder,string fileName)
		{
			int lastTime = -999999;
			int timeStamp;
			int frameCount = 0;

			int endOfHeaderIndex = AedatUtilities.GetEndOfHeaderIndex(ref aedatFile);   // find end of aedat header
			
			string fileConent = "";
			// Read through AEDAT file
			for (int i = endOfHeaderIndex, length = aedatFile.Length; i < length; i += AedatUtilities.dataEntrySize)    // iterate through file, 8 bytes at a time.
			{
                AEDATEvent currentEvent = new AEDATEvent(aedatFile, i, cam);

                fileConent += currentEvent.onOff + "," + currentEvent.x+ "," + currentEvent.y + "," + currentEvent.time + "\n";
                timeStamp = currentEvent.time;

				if (lastTime == -999999)
				{
					lastTime = timeStamp;
				}
				else
				{
					if (lastTime + frameTime <= timeStamp) // Collected events within specified timeframe, add frame to video
					{
						try
						{
							StorageFile file = await folder.CreateFileAsync(fileName + frameCount + ".csv", CreationCollisionOption.GenerateUniqueName);
							await FileIO.WriteTextAsync(file, "On/Off,X,Y,Timestamp\n" + fileConent);
						} catch { }
						
						fileConent = "";
						frameCount++;
						// Stop adding frames to video if max frames has been reached
						if (frameCount >= maxFrames)
						{
							break;
						}
						lastTime = timeStamp;
					}
				}
			}
		}

		public async Task EventBasedReconstruction(byte[] aedatFile, CameraParameters cam, int eventsPerFrame, int maxFrames, StorageFolder folder, string fileName)
		{
			int frameCount = 0;
			int eventCount = 0;
			int endOfHeaderIndex = AedatUtilities.GetEndOfHeaderIndex(ref aedatFile);   // find end of aedat header

			string fileConent = "";
			// Read through AEDAT file
			for (int i = endOfHeaderIndex, length = aedatFile.Length; i < length; i += AedatUtilities.dataEntrySize)    // iterate through file, 8 bytes at a time.
			{
                AEDATEvent currentEvent = new AEDATEvent(aedatFile, i, cam);

				fileConent += $"{currentEvent.onOff},{currentEvent.x},{currentEvent.y},{currentEvent.time}\n";
				eventCount++;
				if (eventCount >= eventsPerFrame) // Collected events within specified timeframe, add frame to video
				{
					eventCount = 0;
					StorageFile file = await folder.CreateFileAsync(fileName + frameCount + ".csv", CreationCollisionOption.GenerateUniqueName);
					await FileIO.WriteTextAsync(file, "On/Off,X,Y,Timestamp\n" + fileConent);

					fileConent = "";
					frameCount++;
					// Stop adding frames to video if max frames has been reached
					if (frameCount >= maxFrames)
					{
						return;
					}
				}
			}
		}

		private (int, int) ParseVideoSettings()
		{
			int frameTime = 33333;  // The amount of time per frame in uS (30 fps = 33333)
			int maxFrames;          // Max number of frames in the reconstructed video
			if (realTimeCheckbox.IsChecked == true)
			{
				frameTime = 33333;
			}
			else
			{
				frameTime = Int32.Parse(frameTimeTB.Text);
			}

			if (allFrameCheckBox.IsChecked == true)
			{
				maxFrames = 2147483647;
			}
			else
			{
				maxFrames = Int32.Parse(maxFramesTB.Text);
			}

			if (maxFrames <= 0 || frameTime <= 0)
			{
				throw new FormatException();
			}

			return (frameTime, maxFrames);
		}

		private void AllFrameCheckBox_Checked(object sender, Windows.UI.Xaml.RoutedEventArgs e)
		{
			this.previousValueMaxFrame = maxFramesTB.Text;
			maxFramesTB.IsReadOnly = true;
			maxFramesTB.IsEnabled = false;
			maxFramesTB.Text = "∞";
		}

		private void AllFrameCheckBox_Unchecked(object sender, Windows.UI.Xaml.RoutedEventArgs e)
		{
			maxFramesTB.Text = this.previousValueMaxFrame;
			maxFramesTB.IsReadOnly = false;
			maxFramesTB.IsEnabled = true;
		}

		private void RealTimeCheckbox_Checked(object sender, Windows.UI.Xaml.RoutedEventArgs e)
		{
			this.previousValueTimePerFrame = frameTimeTB.Text;
			frameTimeTB.Text = "Real Time";
			frameTimeTB.IsReadOnly = true;
			frameTimeTB.IsEnabled = false;
		}

		private void RealTimeCheckbox_Unchecked(object sender, Windows.UI.Xaml.RoutedEventArgs e)
		{
			frameTimeTB.Text = this.previousValueTimePerFrame;
			frameTimeTB.IsReadOnly = false;
			frameTimeTB.IsEnabled = true;
		}

		private void PlaybackType_Toggled(object sender, Windows.UI.Xaml.RoutedEventArgs e)
		{
			try
			{
				if (playbackType.IsOn)
				{
					numOfEventInput.IsEnabled = false;
					realTimeCheckbox.IsEnabled = true;
					frameTimeTB.IsEnabled = true;
				}
				else
				{
					numOfEventInput.IsEnabled = true;
					realTimeCheckbox.IsEnabled = false;
					frameTimeTB.IsEnabled = false;
				}
			}
			catch
			{

			}
		}
	}
}
