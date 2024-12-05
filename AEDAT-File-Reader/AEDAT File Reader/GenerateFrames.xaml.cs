﻿using AEDAT_File_Reader.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Imaging;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=234238

namespace AEDAT_File_Reader
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class GenerateFrames : Page
    {
        public ObservableCollection<EventColor> colors;
        public GenerateFrames()
        {

            colors = new ObservableCollection<EventColor>
            {
                new EventColor("Green", EventColor.Green),
                new EventColor("Red", EventColor.Red),
                new EventColor("Blue", EventColor.Blue),
                new EventColor("Gray", EventColor.Gray),
                new EventColor("White", EventColor.White),
                new EventColor("Black", EventColor.Black)
            };
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
            EventColor onColor;
            EventColor offColor;
            int frameTime;
            int maxFrames;

            try
            {
                // Grab video reconstruction settings from GUI
                (frameTime, maxFrames, onColor, offColor) = ParseFrameSettings();
            }
            catch (FormatException)
            {
                await invaldVideoSettingsDialog.ShowAsync();
                return;
            }

            // Use FolderPicker to select the root folder
            var folderPicker = new FolderPicker
            {
                ViewMode = PickerViewMode.Thumbnail,
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };
            folderPicker.FileTypeFilter.Add("*"); // Allow selecting any folder

            StorageFolder rootFolder = await folderPicker.PickSingleFolderAsync();
            if (rootFolder == null)
            {
                Debug.WriteLine("No folder was selected.");
                return;
            }

            try
            {
                showLoading.IsActive = true;
                backgroundTint.Visibility = Windows.UI.Xaml.Visibility.Visible;

                // Recursively find and process .aedat files
                await ProcessFolderAsync(rootFolder, onColor, offColor, frameTime, maxFrames);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during folder processing: {ex.Message}");
            }
            finally
            {
                showLoading.IsActive = false;
                backgroundTint.Visibility = Windows.UI.Xaml.Visibility.Collapsed;
            }
        }

        // Recursively process a folder to find .aedat files
        private async Task ProcessFolderAsync(StorageFolder folder, EventColor onColor, EventColor offColor, int frameTime, int maxFrames)
        {
            try
            {
                // Get all files and subfolders
                var items = await folder.GetItemsAsync();
                foreach (var item in items)
                {
                    if (item is StorageFile file && file.FileType.Equals(".aedat", StringComparison.OrdinalIgnoreCase))
                    {
                        // Process the .aedat file
                        await ProcessFileAsync(file, onColor, offColor, frameTime, maxFrames);
                    }
                    else if (item is StorageFolder subfolder)
                    {
                        // Recursively process subfolders
                        await ProcessFolderAsync(subfolder, onColor, offColor, frameTime, maxFrames);
                    }
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                Debug.WriteLine($"Access denied to folder {folder.Path}: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error processing folder {folder.Path}: {ex.Message}");
            }
        }

        // Process an individual .aedat file
        private async Task ProcessFileAsync(StorageFile file, EventColor onColor, EventColor offColor, int frameTime, int maxFrames)
        {
            try
            {
                Debug.WriteLine($"Processing file: {file.Path}");

                var headerData = await AedatUtilities.GetHeaderData(file);
                var aedatFile = (await file.OpenReadAsync()).AsStreamForRead();
                aedatFile.Seek(headerData.Item1, SeekOrigin.Begin); // Skip over header.

                CameraParameters cam = headerData.Item2;
                if (cam == null)
                {
                    Debug.WriteLine("Camera parameters are null, skipping file.");
                    return;
                }

                // Get the folder where the file is located
                var parentFolder = await file.GetParentAsync();
                if (parentFolder == null)
                {
                    Debug.WriteLine($"Cannot determine parent folder for file: {file.Path}");
                    return;
                }

                // Create an output folder in the same directory as the .aedat file
                var outputFolderName = file.Name.Replace(".aedat", "") +
                                       (playbackType.IsOn ? " Time Based" : " Event Based") +
                                       " Frames";
                StorageFolder outputFolder = await parentFolder.CreateFolderAsync(outputFolderName, CreationCollisionOption.GenerateUniqueName);

                // Perform reconstruction
                if (playbackType.IsOn)
                {
                    Debug.WriteLine("Performing Time-Based Reconstruction");
                    await TimeBasedReconstruction(aedatFile, cam, onColor, offColor, frameTime, maxFrames, outputFolder, file.Name.Replace(".aedat", ""));
                }
                else
                {
                    Debug.WriteLine("Performing Event-Based Reconstruction");
                    int numOfEvents = Int32.Parse(numOfEventInput.Text);
                    await EventBasedReconstruction(aedatFile, cam, onColor, offColor, numOfEvents, maxFrames, outputFolder, file.Name.Replace(".aedat", ""));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error processing file {file.Path}: {ex.Message}");
            }
        }





        private static Stream InitBitMap(CameraParameters cam)
        {
            // Initilize writeable bitmap
            WriteableBitmap bitmap = new WriteableBitmap(cam.cameraX, cam.cameraY); // init image with camera size
            Stream pixelStream = bitmap.PixelBuffer.AsStream();
            return pixelStream;
        }

        public async Task TimeBasedReconstruction(Stream aedatFile, CameraParameters cam, EventColor onColor, EventColor offColor, int frameTime, int maxFrames, StorageFolder folder,string fileName)
        {
			byte[] aedatBytes = new byte[5 * Convert.ToInt32(Math.Pow(10, 8))]; // Read 0.5 GB at a time
			int lastTime = -999999;
            int timeStamp;
            int frameCount = 0;
            Stream pixelStream = InitBitMap(cam);
            byte[] currentFrame = new byte[pixelStream.Length];

			int bytesRead = aedatFile.Read(aedatBytes, 0, aedatBytes.Length);

			while (bytesRead != 0 && frameCount < maxFrames)
			{
				// Read through AEDAT file
				for (int i = 0, length = bytesRead; i < length; i += AedatUtilities.dataEntrySize)    // iterate through file, 8 bytes at a time.
				{
					AEDATEvent currentEvent = new AEDATEvent(aedatBytes, i, cam);

					timeStamp = currentEvent.time;
					AedatUtilities.SetPixel(ref currentFrame, currentEvent.x, currentEvent.y, (currentEvent.onOff ? onColor.Color : offColor.Color), cam.cameraX);

					if (lastTime == -999999)
					{
						lastTime = timeStamp;
					}
					else
					{
						if (lastTime + frameTime <= timeStamp) // Collected events within specified timeframe, add frame to video
						{
							WriteableBitmap b = new WriteableBitmap(cam.cameraX, cam.cameraY);
							using (Stream stream = b.PixelBuffer.AsStream())
							{
								await stream.WriteAsync(currentFrame, 0, currentFrame.Length);
							}
							try
							{
								var file = await folder.CreateFileAsync(fileName + frameCount + ".png", CreationCollisionOption.GenerateUniqueName);
								using (IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite))
								{
									BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
									Stream pixelStream2 = b.PixelBuffer.AsStream();
									byte[] pixels = new byte[pixelStream2.Length];
									await pixelStream2.ReadAsync(pixels, 0, pixels.Length);

									encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
														(uint)b.PixelWidth,
														(uint)b.PixelHeight,
														96.0,
														96.0,
														pixels);
									await encoder.FlushAsync();
								}
							}
							catch { }

							frameCount++;
							// Stop adding frames to video if max frames has been reached
							if (frameCount >= maxFrames)
							{
								break;
							}
							currentFrame = new byte[pixelStream.Length];
							lastTime = timeStamp;
						}
					}
				}
				bytesRead = aedatFile.Read(aedatBytes, 0, aedatBytes.Length);
			}
        }

        public async Task EventBasedReconstruction(Stream aedatFile, CameraParameters cam, EventColor onColor, EventColor offColor, int eventsPerFrame, int maxFrames, StorageFolder folder, string fileName)
        {
			byte[] aedatBytes = new byte[5 * Convert.ToInt32(Math.Pow(10, 8))]; // Read 0.5 GB at a time
			int frameCount = 0;
            int eventCount = 0;
            Stream pixelStream = InitBitMap(cam);
            byte[] currentFrame = new byte[pixelStream.Length];

			int bytesRead = aedatFile.Read(aedatBytes, 0, aedatBytes.Length);

			while (bytesRead != 0 && frameCount < maxFrames)
			{
				// Read through AEDAT file
				for (int i = 0, length = bytesRead; i < length; i += AedatUtilities.dataEntrySize)    // iterate through file, 8 bytes at a time.
				{
					AEDATEvent currentEvent = new AEDATEvent(aedatBytes, i, cam);

					AedatUtilities.SetPixel(ref currentFrame, currentEvent.x, currentEvent.y, (currentEvent.onOff ? onColor.Color : offColor.Color), cam.cameraX);

					eventCount++;
					if (eventCount >= eventsPerFrame) // Collected events within specified timeframe, add frame to video
					{
						eventCount = 0;
						WriteableBitmap b = new WriteableBitmap(cam.cameraX, cam.cameraY);
						using (Stream stream = b.PixelBuffer.AsStream())
						{
							await stream.WriteAsync(currentFrame, 0, currentFrame.Length);
						}
						var file = await folder.CreateFileAsync(fileName + frameCount + ".png");
						using (IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite))
						{
							BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
							Stream pixelStream2 = b.PixelBuffer.AsStream();
							byte[] pixels = new byte[pixelStream2.Length];
							await pixelStream2.ReadAsync(pixels, 0, pixels.Length);

							encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
												(uint)b.PixelWidth,
												(uint)b.PixelHeight,
												96.0,
												96.0,
												pixels);
							await encoder.FlushAsync();
						}
						frameCount++;
						// Stop adding frames to video if max frames has been reached
						if (frameCount >= maxFrames) return;

						currentFrame = new byte[pixelStream.Length];
					}
				}
				bytesRead = aedatFile.Read(aedatBytes, 0, aedatBytes.Length);
			}
            return;
        }

        private (int, int, EventColor, EventColor) ParseFrameSettings()
        {
            int frameTime = 33333;  // The amount of time per frame in uS (30 fps = 33333)
            int maxFrames;          // Max number of frames in the reconstructed video

            if (!playbackType.IsOn)
            {
                frameTime = Int32.Parse(frameTimeTB.Text);
            }


            maxFrames = allFrameCheckBox.IsChecked == true ? 2147483647 : Int32.Parse(maxFramesTB.Text);

			if (maxFrames <= 0 || frameTime <= 0)
            {
                throw new FormatException();
            }

            // Grab ON and OFF colors from comboBox
            EventColor onColor = onColorCombo.SelectedItem as EventColor;
            EventColor offColor = offColorCombo.SelectedItem as EventColor;

            return (frameTime, maxFrames, onColor, offColor);
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

        private void Page_Loaded(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            onColorCombo.SelectedIndex = 0;
            offColorCombo.SelectedIndex = 1;
        }

       

        private void PlaybackType_Toggled(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            try
            {
                if (playbackType.IsOn)
                {
                    numOfEventInput.IsEnabled = false;
                    frameTimeTB.IsEnabled = true;
                }
                else
                {
                    numOfEventInput.IsEnabled = true;
                    frameTimeTB.IsEnabled = false;
                }
            }
            catch
            {

            }
        }
    }
}
