using AuxControls;
using C1.WPF.FlexGrid;
using C1.WPF.Toolbar;
using Controllers.Receiver;
using Gnss;
using NetInterfaces;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using Utilities;

namespace ReceiverControls
{
    /// <summary>
    /// Interaction logic for FileViewControl.xaml
    /// </summary>
    public partial class FileViewControl : System.Windows.Controls.UserControl
    {
        private readonly Lazy<string[]> _antennas;
        CancellationTokenSource _cancellationTokenSource = null;

        //   int taskCounter = 0;
        private bool _isBusy = false;
        private bool paramInited;

        public FileViewControl()
        {
            InitializeComponent();
            foreach (var key in Resources.Keys)
            {
                if (!(Resources[key] is C1ToolbarCommand cmd))
                {
                    continue;
                }

                string keyStr = key.ToString();
                CommandManager.RegisterClassCommandBinding(GetType(), new CommandBinding(
                    cmd, async (s, e) => await Execute(keyStr, e.OriginalSource as FrameworkElement, e.Parameter), (s, e) => e.CanExecute = true));
            }
            _antennas = new Lazy<string[]>(() =>
            {
                string dbName = @"Resources\antennas.db3";
                try
                {
                    var assemblyPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    if (assemblyPath == null)
                    {
                        return new string[0];
                    }

                    Logger.TraceInfo(Path.Combine(assemblyPath, dbName));
                    var antennaList = HubDbUtilities.GetAntennasType(Path.Combine(assemblyPath, dbName)).ToList();
                    antennaList.Insert(0, "ADVNULLANTENNA");
                    return antennaList.ToArray();
                }
                catch (Exception ex)
                {
                    Logger.TraceError(ex);
                    return new string[0];
                }
            });
            dataGrid.CellFactory = new StandardCellFactory();
            msgs.ItemsSource = Gnss.Constants.MessagesSet;
            intervalNb.Text = 1.0.ToString(CultureInfo.InvariantCulture);
        }
        private async Task Execute(string key, FrameworkElement source, object parameter)
        {
            if (!(DataContext is IGnssViewModel model))
            {
                return;
            }
            //    CancelRefresh();
            //
            //   taskCounter++;
            Logger.TraceInfo(@"File view Execute");
            model.ClearAction();
            OperationResult result = OperationResult.Sucsess;
            try
            {
                _isBusy = true;
                switch (key)
                {
                    case "stopRec":
                        result = await StopRec(model, parameter.ToString().ToCharArray()[0]);
                        break;
                    case "cancelDown":
                        CancelDownLoading(model, source);
                        break;
                    case "delFile":
                        result = await DeleteFile(model, source);
                        break;
                    case "downloadFile":
                        await DownloadFile(model, source);
                        break;
                    case "delFiles":
                        result = await DeleteFiles(model);
                        break;
                    case "delAllFiles":
                        result = await DeleteAllFiles(model);
                        break;
                    case "downloadFiles":
                        await DownloadFiles(model);
                        break;
                    case "cancelDowns":
                        CancelDownLoadingFiles(model);
                        break;
                    case "startFile":
                        result = await StartNewFile(model, parameter.ToString().ToCharArray()[0]);
                        break;
                    default:
                        return;
                }
                //if (result == OperationResult.Started)
                //    return;
                if (result == OperationResult.Canceled)
                {
                    model.FinishCancel();
                }
                else
                {
                    model.FinishAction(result);
                }
            }
            catch (Exception ex)
            {
                model.FinishError(ex.Message);
                Debug.WriteLine(ex);
                Logger.TraceError(ex);
                _isBusy = false;
            }
            finally
            {
              //  taskCounter--;
                Debug.WriteLine(@"File View finally");

                //if (taskCounter == 0)
                //{
                // Logger.TraceInfo(@"File Execute");
                //_ = StartUpdating();
                if(IsVisible)
                {
                    await UpdateFiles(model);
                }

                _isBusy = false;
             //   }


                ////    await RefreshFileList();
            }
        }

        private async Task UpdateFiles(IGnssViewModel model)
        {
            await model.UpdateInfoParamsAsync();
            await model.RefreshFileList();
        }
        private async Task<OperationResult> DeleteAllFiles(IGnssViewModel model)
        {
            if (UIUtilities.ShowConfirmationMessageBox(string.Format(CultureInfo.CurrentCulture,
                      LocalResources.Properties.Resources.FileDeleteConfirmation, model.JpsFiles.Count))
                        == MessageBoxResult.No)
            {
                return OperationResult.Canceled;
            }

            model.StartAction(ReceiverAction.DeleteFiles);
            return await model.DeleteAllFiles() ? OperationResult.Sucsess : OperationResult.Error;
        }
        private OperationResult GetActionResult(IGnssViewModel model, string reply)
        {
            if (string.IsNullOrEmpty(reply))
            {
                return OperationResult.Sucsess;
            }

            model.FinishError(reply);
            return OperationResult.Error;
        }
        private async Task<OperationResult> StartNewFile(IGnssViewModel model, char port)
        {
            string fileName = startfileName.Text;
            FileAction fileAction = string.IsNullOrEmpty(fileName) ? FileAction.Start : await GetFileAction(model, fileName);
            if (fileAction == FileAction.Owerwrite)
            {
                if (await DeleteFile(model, fileName) != OperationResult.Sucsess)
                {
                    return OperationResult.Error;
                }
                model.FinishAction(OperationResult.Sucsess);
            }
            model.StartAction(ReceiverAction.StartFile);
            int mask = (int)maskNb.Value;
            //    double interval = intervalNb.Text;
            double.TryParse(intervalNb.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double interval);
        //    double interval = intervalNb.Counter;
            string msgset = msgs.SelectedValue.ToString();

            string replyPar = await model.SetCurFileParams(port, mask, interval, msgset);
            if (replyPar.Contains("msgs"))
            {
                replyPar = string.Empty;
            }

            if (GetActionResult(model, replyPar) == OperationResult.Error)
            {
                return OperationResult.Error;
            }

            if (fileAction == FileAction.Append)
            {
                string reply = await model.SetCurFile(fileName, port);
                if (GetActionResult(model, reply) == OperationResult.Error)
                {
                    return OperationResult.Error;
                }
            }
            else
            {
                string reply = await model.CreateFile(fileName, port);
                if (GetActionResult(model, reply) == OperationResult.Error)
                {
                    return OperationResult.Error;
                }
            }

            //bool res1 = await model.SetCurFileParams(fileL, mask, interval);
            //bool res2 = await model.StartFile(fileL);

            string replyStart = await model.StartFile(port, interval, msgset);
            if (GetActionResult(model, replyPar) == OperationResult.Error)
            {
                return OperationResult.Error;
            }

            //     Debug.WriteLine(@"Start file " + res1 + @"  " + res2);

            if (freeEv.IsChecked.HasValue && (bool)freeEv.IsChecked)
            {
                _ = await model.SendFreeEvent(site.Text, antId.SelectedItem.ToString(),
                    height.Value.ToString(CultureInfo.InvariantCulture), slant.IsChecked.HasValue && (bool)slant.IsChecked);
            }

        //    await model.UpdateFileList();
            return OperationResult.Sucsess;
        }
        private async Task<FileAction> GetFileAction(IGnssViewModel model, string fileName)
        {
            if (!await model.CheckFileExists(fileName))
            {
                return FileAction.Start;
            }

            var messageBox = new NetMessageBox(fileName);
            messageBox.ShowDialog();
            return messageBox.FileAppendResult;
        }
        private async Task<OperationResult> DeleteFiles(IGnssViewModel model)
        {
            var jpsFiles = dataGrid.SelectedItems.Cast<JpsFile>();
            if (jpsFiles == null || jpsFiles.Count() == 0)
            {
                return OperationResult.None;
            }

            if (UIUtilities.ShowConfirmationMessageBox(string.Format(CultureInfo.CurrentCulture,
                LocalResources.Properties.Resources.FileDeleteConfirmation, jpsFiles.Count()))
                == MessageBoxResult.No)
            {
                return OperationResult.Canceled;
            }

            model.StartAction(ReceiverAction.DeleteFiles);
            bool result = await model.DeleteFiles(jpsFiles.Select(j => j.Name).ToArray());
            dataGrid.Select(-1, -1);
        //    await model.UpdateFileList();

            return result ? OperationResult.Sucsess : OperationResult.Error;
        }
        private async Task DownloadFiles(IGnssViewModel model)
        {
            var jpsFiles = dataGrid.SelectedItems.Cast<JpsFile>();
            if (jpsFiles == null || jpsFiles.Count() == 0)
            {
                return;
            }

            var jpsFilesArray = jpsFiles.ToArray();

            if (jpsFilesArray.Length == 1)
            {
                await DownloadSingleFile(model, jpsFilesArray[0]);
                return;
            }

            PrepareDownloadFiles(jpsFiles.ToArray());
        //    model.SetCashTasks();
            await model.StartDownload();
        }
        private async Task DownloadFile(IGnssViewModel model, FrameworkElement source)
        {
            Debug.WriteLine(@"DownloadFile");
            if (source == null)
            {
                return;
            }

            if (!(source.DataContext is JpsFile jpsFile))
            {
                return;
            }

            await DownloadSingleFile(model, jpsFile);
        }
        private async Task DownloadSingleFile(IGnssViewModel model, JpsFile jpsFile)
        {
            PrepareDownloadFile(jpsFile);
            await model.StartDownload();
        }
        private static void PrepareDownloadFile(JpsFile jpsFile)
        {
            if (jpsFile.State == JpsFileState.Cashing)
            {
                return;
            }

            string path = GetDownloadPath(jpsFile.Name);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            jpsFile.Path = path;
            jpsFile.State = JpsFileState.Waiting;
            jpsFile.ProcessingResult = OperationResult.None;
            jpsFile.Info = string.Empty;
            jpsFile.Cashed = 0;
        }
        private void PrepareDownloadFiles(JpsFile[] jpsFiles)
        {
            string folder = GetDownloadFolder();
            if (string.IsNullOrEmpty(folder))
            {
                return;
            }

            foreach (var f in jpsFiles.Where(j=>j.State != JpsFileState.Cashing))
            {
                string path = Path.Combine(folder, f.Name);
                f.Path = string.IsNullOrEmpty(Path.GetExtension(path)) ?
                    Path.ChangeExtension(path, "jps") : path;
                f.State = JpsFileState.Waiting;
                f.ProcessingResult = OperationResult.None;
            }
        }
        private static string GetDownloadPath(string fileName)
        {
            string name = string.IsNullOrEmpty(Path.GetExtension(fileName)) ? Path.ChangeExtension(fileName, "jps") : fileName;
            var saveFileDialog = new SaveFileDialog
            {
            //    Filter = LocalResources.Properties.Resources.JpsFileString,//"JPS file|*.jps",
                Title = LocalResources.Properties.Resources.SavingPath, //"Saving file",
                InitialDirectory = Config.Instance.LastPathDownload,
                FileName = name
            };
            DialogResult res = saveFileDialog.ShowDialog();
            if (res != DialogResult.OK)
            {
                return string.Empty;
            }

            Config.Instance.LastPathDownload = Path.GetDirectoryName(saveFileDialog.FileName);
            return saveFileDialog.FileName;
        }

        private string GetDownloadFolder()
        {
            var openFolderDilog = new FolderBrowserDialog { ShowNewFolderButton = true, SelectedPath = Config.Instance.LastRtFolder};
            //   openFolderDilog.RootFolder = Path.GetDirectoryName(Config.Instance.LastPathDownloadedFile);
            if (openFolderDilog.ShowDialog() != DialogResult.OK)
            {
                return string.Empty;
            }
            Config.Instance.LastRtFolder = openFolderDilog.SelectedPath;
            return openFolderDilog.SelectedPath;
        }
        private async Task<OperationResult> DeleteFile(IGnssViewModel model, FrameworkElement source)
        {
            Debug.WriteLine(@"DeleteFile");
            if (source == null)
            {
                return OperationResult.None;
            }

            if (!(source.DataContext is JpsFile jpsFile))
            {
                return OperationResult.None;
            }

            if (UIUtilities.ShowConfirmationMessageBox(string.Format(CultureInfo.CurrentCulture,
                LocalResources.Properties.Resources.SingleFileDeleteConfirmation, jpsFile.Name))
                == MessageBoxResult.No)
            {
                return OperationResult.Canceled;
            }

            var res = await DeleteFile(model, jpsFile.Name);

            if (res == OperationResult.Sucsess)
            {
                if(dataGrid.SelectedItems.Count > 0)
                {
                    dataGrid.SelectedItems.RemoveAt(dataGrid.SelectedItems.Count - 1);
                }
            }
            return res;

        }

        private async Task<OperationResult> DeleteFile(IGnssViewModel model, string fileName)
        {
            model.StartAction(ReceiverAction.DeleteFiles);
            bool result = await model.DeleteFile(fileName);
        //    await model.UpdateFileList();
            //if (result)
            //{
            //    await model.UpdateFileList();
            //    return OperationResult.Sucsess;
            //}

            return result ? OperationResult.Sucsess : OperationResult.Error; ;
        }

        private void CancelDownLoading(IGnssViewModel model, FrameworkElement source)
        {
            Debug.WriteLine(@"CancelDownLoading");
            if (source == null)
                return;
            if (!(source.DataContext is JpsFile jpsFile))
                return;

            CancelDownLoadingFiles(model, new JpsFile[] { jpsFile });
        }

        private void CancelDownLoadingFiles(IGnssViewModel model)
        {
            var jpsFiles = dataGrid.SelectedItems.Cast<JpsFile>();
            if (jpsFiles == null || jpsFiles.Count() == 0)
            {
                return;
            }

            CancelDownLoadingFiles(model, jpsFiles.ToArray());
        }

        private void CancelDownLoadingFiles(IGnssViewModel model, JpsFile[] jpsFiles) =>
                        model.CancelDownLoadingFiles(jpsFiles);

        private async Task<OperationResult> StopRec(IGnssViewModel model, char port)
        {
            Debug.WriteLine(@"StopRec");

            model.StartAction(ReceiverAction.StopFile);

            string reply = await model.StoptFile(port);
            return GetActionResult(model, reply);
            //if (result == OperationResult.Sucsess)
            //{
            //    await model.UpdateFileList();
            //    return OperationResult.Sucsess;
            //}

            //return OperationResult.Error;
        }
        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            Logger.TraceInfo(@"File loaded");
            //  await Task.Delay(2000);
            //   await RefreshFileList();

            _ = StartUpdating(TimeSpan.FromSeconds(5));
        }
        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            Logger.TraceInfo(@"FileView Unloaded");
            CancelRefresh();
        }
        internal void CancelRefresh()
        {
            Logger.TraceInfo(@"Stop Updating");
            if (_cancellationTokenSource == null)
            {
                return;
            }

            if (_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = null;
                return;
            }

            TaskUtilities.CancelToken(_cancellationTokenSource);

        }
        //internal async Task RefreshFileList()
        //{
        //    if (!(DataContext is IGnssViewModel model))
        //        return;
        //    CancelRefresh();
        //    await model.RefreshFileList();
        //    _ = StartUpdating(TimeSpan.FromSeconds(5));
        //}
        internal async Task StartUpdating(TimeSpan period)
        {
            if (!(DataContext is IGnssViewModel model))
            {
                return;
            }
            //   Debug.WriteLine(@"StartUpdating");
            if (!paramInited)
            {
                await InitFileRecordParamsFromA(model);
                paramInited = true;
            }

            CancelRefresh();
            if (IsVisible && !_isBusy)
            {
                await UpdateFiles(model);
            }

            Logger.TraceInfo(@"StartUpdating");
            using (_cancellationTokenSource = new CancellationTokenSource())
            {
                //   await model.UpdateFileList(TimeSpan.FromSeconds(5), _cancellationTokenSource.Token);
                while (true)
                {
                    try
                    {
                        _cancellationTokenSource.Token.ThrowIfCancellationRequested();
                        if (_cancellationTokenSource.IsCancellationRequested)
                        {
                            break;
                        }
                        await Task.Delay(period, _cancellationTokenSource.Token);
                    }
                    catch (TaskCanceledException ex)
                    {
                        Debug.WriteLine(ex);
                        Logger.TraceError(ex);
                        break;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex);
                        Logger.TraceError(ex);
                        break;
                    }
                    Logger.TraceInfo(@"Updating");
                    if (IsVisible && !_isBusy)
                    {
                        await UpdateFiles(model);
                    }
                    period = IsVisible ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(1);
                }
            }

            //if(_cancellationTokenSource != null)
            //{
            //    _cancellationTokenSource.Dispose();
            //    _cancellationTokenSource = null;
            //}
        }

        private async Task InitFileRecordParamsFromA(IGnssViewModel model)
        {
            var curFilePars = await model.GetCurFileParams('a');
            if(curFilePars == null)
            {
                return;
            }
            if(!string.IsNullOrEmpty(curFilePars[0]) && double.TryParse(curFilePars[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double mask))
            {
                maskNb.Value = mask;
            }
            if (!string.IsNullOrEmpty(curFilePars[1]))
            {
                intervalNb.Text = curFilePars[1].Trim().ToString(CultureInfo.InvariantCulture);
            }
            if (!string.IsNullOrEmpty(curFilePars[2]))
            {
                msgs.SelectedItem = curFilePars[2].Trim().ToString(CultureInfo.InvariantCulture);
            }
        }

        private void FreeEv_Checked(object sender, RoutedEventArgs e)
        {
            if (antId.ItemsSource == null)
            {
                antId.ItemsSource = _antennas.Value;
            }

            if (string.IsNullOrEmpty(site.Text))
            {
                if (!(DataContext is IGnssViewModel model))
                {
                    return;
                }

                site.Text = model.Model;
            }
        }
        private void C1FlexGrid_SelectionChanged(object sender, CellRangeEventArgs e)
        {
            if (!(DataContext is IGnssViewModel model))
            {
                return;
            }

            var waitingFiles = model.JpsFiles.Where(f => f.State == JpsFileState.Waiting && !dataGrid.SelectedItems.Contains(f)).ToList();
            waitingFiles.ForEach(wf => wf.State = JpsFileState.None);
            Debug.WriteLine(@"C1FlexGrid_SelectionChanged");
        }
        private void DataGrid_SelectionChanging(object sender, CellRangeEventArgs e)
        {
            Debug.WriteLine(@"DataGrid_SelectionChanging " + e.CellRange.IsSingleCell + @" " + e.Column);
            if (e.CellRange.IsSingleCell && e.Column != -1)
            {
                //  if (e.CellRange.LeftColumn > -1)
                e.Cancel = true;
            }
            //else if (e.CellRange.LeftColumn > 0)
            //    e.Cancel = true;
        }

        private void intervalNb_ValueChanged(object sender, C1.WPF.PropertyChangedEventArgs<double> e)
        {
            Debug.WriteLine(@"intervalNb_ValueChanged ");
        }
    }
}
