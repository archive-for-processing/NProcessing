﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using System.IO;
using System.Diagnostics;
using NLog;
using NProcessing.Script;


namespace NProcessing
{
    public partial class MainForm : Form
    {
        #region Enums
        /// <summary>Internal status.</summary>
        enum PlayCommand { Start, Stop }
        #endregion

        #region Fields
        /// <summary>My logger.</summary>
        Logger _logger = LogManager.GetCurrentClassLogger();

        /// <summary>Fast timer.</summary>
        MmTimerEx _timer = new MmTimerEx();

        /// <summary>Surface child form.</summary>
        Surface _surface = new Surface();

        /// <summary>Current script file name.</summary>
        string _fn = Utils.UNKNOWN_STRING;

        /// <summary>The current script.</summary>
        NpScript _script = null;

        /// <summary>Frame rate in fps.</summary>
        int _frameRate = 30;

        /// <summary>Seconds since start pressed.</summary>
        DateTime _startTime = DateTime.Now;

        /// <summary>Script compile errors and warnings.</summary>
        List<ScriptError> _compileResults = new List<ScriptError>();

        /// <summary>Detect changed script files.</summary>
        MultiFileWatcher _watcher = new MultiFileWatcher();

        /// <summary>Files that have been changed externally or have runtime errors - requires a recompile.</summary>
        bool _needCompile = false;

        /// <summary>The temp dir for channeling down runtime errors.</summary>
        string _compileTempDir = "";
        #endregion

        #region Lifecycle
        /// <summary>
        /// Constructor.
        /// </summary>
        public MainForm()
        {
            // Need to load settings before creating controls in MainForm_Load().
            string appDir = Utils.GetAppDataDir();
            DirectoryInfo di = new DirectoryInfo(appDir);
            di.Create();
            UserSettings.Load(appDir);
            InitializeComponent();
//            toolStrip1.Renderer = new Common.CheckBoxRenderer(); // for checked color.
        }

        /// <summary>
        /// Initialize form controls.
        /// </summary>
        void MainForm_Load(object sender, EventArgs e)
        {
            txtView.Font = UserSettings.TheSettings.EditorFont;
            txtView.BackColor = UserSettings.TheSettings.BackColor;


            // btnSettings.Image = Utils.ColorizeBitmap(btnSettings.Image, UserSettings.TheSettings.IconColor);
            // btnAbout.Image = Utils.ColorizeBitmap(btnAbout.Image, UserSettings.TheSettings.IconColor);
            // fileDropDownButton.Image = Utils.ColorizeBitmap(fileDropDownButton.Image, UserSettings.TheSettings.IconColor);
            // btnRewind.Image = Utils.ColorizeBitmap(btnRewind.Image, UserSettings.TheSettings.IconColor);
            // btnCompile.Image = Utils.ColorizeBitmap(btnCompile.Image, UserSettings.TheSettings.IconColor);

            // chkPlay.Image = Utils.ColorizeBitmap(chkPlay.Image, UserSettings.TheSettings.IconColor);
            // chkPlay.BackColor = UserSettings.TheSettings.BackColor;
            // chkPlay.FlatAppearance.CheckedBackColor = UserSettings.TheSettings.SelectedColor;
            

            btnClear.Click += (object _, EventArgs __) => { txtView.Clear(); };
            btnWrap.Click += (object _, EventArgs __) => { txtView.WordWrap = btnWrap.Checked; };

            // Init UI from settings
            Location = new Point(UserSettings.TheSettings.MainFormInfo.X, UserSettings.TheSettings.MainFormInfo.Y);
            Size = new Size(UserSettings.TheSettings.MainFormInfo.Width, UserSettings.TheSettings.MainFormInfo.Height);
            WindowState = FormWindowState.Normal;

            _surface.Visible = true;
            _surface.Location = new Point(Right, Top);
            _surface.TopMost = UserSettings.TheSettings.LockUi;

            InitLogging();

            PopulateRecentMenu();

            KeyPreview = true; // for routing kbd strokes properly

            _watcher.FileChangeEvent += Watcher_Changed;

            Text = $"NProcessing {Utils.GetVersionString()} - No file loaded";

            // Catches runtime errors during drawing.
            _surface.RuntimeErrorEvent += (object esender, Surface.RuntimeErrorEventArgs eargs) => { ScriptRuntimeError(eargs); };

            // Fast timer.
            _timer.TimerElapsedEvent += TimerElapsedEvent;
            SetUiTimerPeriod();
            _timer.Start();
        }

        /// <summary>
        /// Clean up on shutdown.
        /// </summary>
        void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                ProcessPlay(PlayCommand.Stop, false);

                // Save user settings.
                SaveSettings();
            }
            catch (Exception ex)
            {
                _logger.Error($"Couldn't save the file: {ex.Message}.");
            }
        }

        /// <summary>
        /// Resource clean up.
        /// </summary>
        /// <param name="disposing"></param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer?.Stop();
                _timer?.Dispose();
                _timer = null;

                components?.Dispose();
            }

            base.Dispose(disposing);
        }
        #endregion

        #region Compile
        /// <summary>
        /// Master compiler function.
        /// </summary>
        bool Compile()
        {
            bool ok = true;

            if (_fn == Utils.UNKNOWN_STRING)
            {
                _logger.Warn("No script file loaded.");
                ok = false;
            }
            else
            {
                Compiler compiler = new Compiler();

                // Compile now.
                _script = compiler.Execute(_fn);

                // Update file watcher just in case.
                _watcher.Clear();
                compiler.SourceFiles.ForEach(f => { if (f != "") _watcher.Add(f); });

                // Process errors. Some may be warnings.
                _compileResults = compiler.Errors;
                int errorCount = _compileResults.Count(w => w.ErrorType == ScriptErrorType.Error);

                if (errorCount == 0 && _script != null)
                {
                    SetCompileStatus(true);
                    _compileTempDir = compiler.TempDir;

                    try
                    {
                        // Init surface area.
                        InitRuntime();
                        _surface.InitSurface(_script);
                        ProcessRuntime();
                    }
                    catch (Exception ex)
                    {
                        ScriptRuntimeError(new Surface.RuntimeErrorEventArgs() { Exception = ex });
                        ok = false;
                    }

                    SetCompileStatus(ok);
                }
                else
                {
                    _logger.Warn("Compile failed.");
                    ok = false;
                    ProcessPlay(PlayCommand.Stop, false);
                    SetCompileStatus(false);
                }

                _compileResults.ForEach(r =>
                {
                    _logger.Log(new LogEventInfo()
                    {
                        Level = r.ErrorType == ScriptErrorType.Warning ? LogLevel.Warn : LogLevel.Error,
                        Message = r.ToString()
                    });
                });
            }

            return ok;
        }

        /// <summary>
        /// Update system statuses.
        /// </summary>
        /// <param name="compileStatus"></param>
        void SetCompileStatus(bool compileStatus)
        {
            if (compileStatus)
            {
                btnCompile.BackColor = SystemColors.Control;
                _needCompile = false;
            }
            else
            {
                btnCompile.BackColor = Color.Red;
                _needCompile = true;
            }
        }
        #endregion

        #region Realtime handling
        /// <summary>
        /// Multimedia timer tick handler.
        /// </summary>
        void TimerElapsedEvent(object sender, MmTimerEx.TimerEventArgs e)
        {
            // Kick over to main UI thread.
            BeginInvoke((MethodInvoker)delegate ()
            {
                if (_script != null)
                {
                    NextDraw(e);
                }
            });
        }

        /// <summary>
        /// Output next frame.
        /// </summary>
        /// <param name="e">Information about updates required.</param>
        void NextDraw(MmTimerEx.TimerEventArgs e)
        {
            InitRuntime();

            if (e.ElapsedTimers.Contains("UI") && btnPlay.Checked && !_needCompile)
            {
                try
                {
                    _surface.UpdateSurface();
                }
                catch (Exception ex)
                {
                    ScriptRuntimeError(new Surface.RuntimeErrorEventArgs() { Exception = ex });
                }
            }

            // Process whatever the script did.
            ProcessRuntime();
        }

        /// <summary>
        /// Package up the runtime stuff the script may need. Call this before any script updates.
        /// </summary>
        void InitRuntime()
        {
            _script.Playing = btnPlay.Checked;
            _script.RealTime = (float)(DateTime.Now - _startTime).TotalSeconds;
            _script.FrameRate = _frameRate;
        }

        /// <summary>
        /// Process whatever the script may have done.
        /// </summary>
        void ProcessRuntime()
        {
            if (_script.FrameRate != _frameRate)
            {
                _frameRate = _script.FrameRate;
                SetUiTimerPeriod();
            }
        }

        /// <summary>
        /// Runtime error. Look for ones generated by our script - normal occurrence which the user should know about.
        /// </summary>
        /// <param name="args"></param>
        void ScriptRuntimeError(Surface.RuntimeErrorEventArgs args)
        {
            ProcessPlay(PlayCommand.Stop, false);
            SetCompileStatus(false);

            ScriptError err = ProcessScriptRuntimeError(args, _compileTempDir);

            if (err != null)
            {
                _logger.Error(err.ToString());
            }
        }

        /// <summary>
        /// Runtime error. Look for ones generated by our script - normal occurrence which the user should know about.
        /// </summary>
        /// <param name="args"></param>
        /// <param name="compileDir"></param>
        ScriptError ProcessScriptRuntimeError(Surface.RuntimeErrorEventArgs args, string compileDir)
        {
            ScriptError err = null;

            // Locate the offending frame.
            string srcFile = Utils.UNKNOWN_STRING;
            int srcLine = -1;
            StackTrace st = new StackTrace(args.Exception, true);
            StackFrame sf = null;

            for (int i = 0; i < st.FrameCount; i++)
            {
                StackFrame stf = st.GetFrame(i);
                if (stf.GetFileName() != null && stf.GetFileName().ToUpper().Contains(compileDir.ToUpper()))
                {
                    sf = stf;
                    break;
                }
            }

            if (sf != null)
            {
                // Dig out generated file parts.
                string genFile = sf.GetFileName();
                int genLine = sf.GetFileLineNumber() - 1;

                // Open the generated file and dig out the source file and line.
                string[] genLines = File.ReadAllLines(genFile);

                srcFile = genLines[0].Trim().Replace("//", "");

                int ind = genLines[genLine].LastIndexOf("//");
                if (ind != -1)
                {
                    string sl = genLines[genLine].Substring(ind + 2);
                    int.TryParse(sl, out srcLine);
                }

                err = new ScriptError()
                {
                    ErrorType = ScriptErrorType.Runtime,
                    SourceFile = srcFile,
                    LineNumber = srcLine,
                    Message = args.Exception.Message
                };
            }
            else // unknown?
            {
                err = new ScriptError()
                {
                    ErrorType = ScriptErrorType.Runtime,
                    SourceFile = "",
                    LineNumber = -1,
                    Message = args.Exception.Message
                };
            }

            return err;
        }
        #endregion

        #region Messages and logging
        /// <summary>
        /// Init all logging functions.
        /// </summary>
        void InitLogging()
        { 
            string appDir = Utils.GetAppDataDir();

            FileInfo fi = new FileInfo(Path.Combine(appDir, "log.txt"));
            if(fi.Exists && fi.Length > 100000)
            {
                File.Copy(fi.FullName, fi.FullName.Replace("log.", "log2."), true);
                File.Delete(fi.FullName);
            }

            // Hook to client window.
            LogClientNotificationTarget.ClientNotification += Log_ClientNotification;
        }

        /// <summary>
        /// A message from the logger to display to the user.
        /// </summary>
        /// <param name="msg">The message.</param>
        void Log_ClientNotification(string msg)
        {
            BeginInvoke((MethodInvoker)delegate ()
            {
                string s = $"{msg}{Environment.NewLine}";

                if (txtView.TextLength > 5000)
                {
                    txtView.Select(0, 1000);
                    txtView.SelectedText = "";
                }

                txtView.SelectionBackColor = UserSettings.TheSettings.BackColor;

                txtView.AppendText(s);
                txtView.ScrollToCaret();
            });
        }

        /// <summary>
        /// Show the log file.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void LogShow_Click(object sender, EventArgs e)
        {
            using (Form f = new Form()
            {
                Text = "Log Viewer",
                Size = new Size(900, 600),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(20, 20),
                FormBorderStyle = FormBorderStyle.FixedToolWindow,
                ShowIcon = false,
                ShowInTaskbar = false
            })
            {
                RichTextBox tv = new RichTextBox()
                {
                    Dock = DockStyle.Fill,
                    Font = UserSettings.TheSettings.EditorFont
                };
                f.Controls.Add(tv);

                string appDir = Utils.GetAppDataDir();
                string logFilename = Path.Combine(appDir, "log.txt");
                File.ReadAllLines(logFilename).ForEach(l => tv.AppendText(l + Environment.NewLine));

                f.ShowDialog();
            }
        }
        #endregion

        #region File handling
        /// <summary>
        /// Allows the user to select a np file from file system.
        /// </summary>
        void Open_Click(object sender, EventArgs e)
        {
            OpenFileDialog openDlg = new OpenFileDialog()
            {
                Filter = "NProcessing files (*.np)|*.np",
                Title = "Select a NProcessing file"
            };

            if (openDlg.ShowDialog() == DialogResult.OK)
            {
                OpenFile(openDlg.FileName);
            }
        }

        /// <summary>
        /// The user has asked to open a recent file.
        /// </summary>
        void Recent_Click(object sender, EventArgs e)
        {
            ToolStripMenuItem item = sender as ToolStripMenuItem;
            string fn = sender.ToString();
            OpenFile(fn);
        }

        /// <summary>
        /// Common np file opener.
        /// </summary>
        /// <param name="fn">The np file to open.</param>
        /// <returns>Error string or empty if ok.</returns>
        public string OpenFile(string fn)
        {
            string ret = "";

            try
            {
                _logger.Info($"Reading np file: {fn}");
                _fn = fn;

                AddToRecentDefs(fn);

                bool ok = Compile();
                SetCompileStatus(ok);

                Text = $"NProcessing {Utils.GetVersionString()} - {fn}";
            }
            catch (Exception ex)
            {
                ret = $"Couldn't open the np file: {fn} because: {ex.Message}";
                _logger.Error(ret);
                SetCompileStatus(false);
            }

            return ret;
        }

        /// <summary>
        /// Create the menu with the recently used files.
        /// </summary>
        void PopulateRecentMenu()
        {
            ToolStripItemCollection menuItems = recentToolStripMenuItem.DropDownItems;
            menuItems.Clear();

            UserSettings.TheSettings.RecentFiles.ForEach(f =>
            {
                ToolStripMenuItem menuItem = new ToolStripMenuItem(f, null, new EventHandler(Recent_Click));
                menuItems.Add(menuItem);
            });
        }

        /// <summary>
        /// Update the mru with the user selection.
        /// </summary>
        /// <param name="fn">The selected file.</param>
        void AddToRecentDefs(string fn)
        {
            if (File.Exists(fn))
            {
                UserSettings.TheSettings.RecentFiles.UpdateMru(fn);
                PopulateRecentMenu();
            }
        }

        /// <summary>
        /// One or more np files have changed so reload/compile.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void Watcher_Changed(object sender, MultiFileWatcher.FileChangeEventArgs e)
        {
            // Kick over to main UI thread.
            BeginInvoke((MethodInvoker)delegate ()
            {
                SetCompileStatus(false);
            });
        }
        #endregion

        #region Main toolbar controls
        /// <summary>
        /// Go or stop button.
        /// </summary>
        void Play_Click(object sender, EventArgs e)
        {
            ProcessPlay(btnPlay.Checked ? PlayCommand.Start : PlayCommand.Stop, true);
        }

        /// <summary>
        /// Manual recompile.
        /// </summary>
        void Compile_Click(object sender, EventArgs e)
        {
            Compile();
            ProcessPlay(PlayCommand.Stop, true);
        }
        #endregion

        #region User settings
        /// <summary>
        /// Save user settings that aren't automatic.
        /// </summary>
        void SaveSettings()
        {
            UserSettings.TheSettings.MainFormInfo.FromForm(this);

            UserSettings.TheSettings.Save();
        }

        /// <summary>
        /// Edit the options in a property grid.
        /// </summary>
        void Settings_Click(object sender, EventArgs e)
        {
            using (Form f = new Form()
            {
                Text = "User Settings",
                Size = new Size(350, 400),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(200, 200),
                FormBorderStyle = FormBorderStyle.FixedToolWindow,
                ShowIcon = false,
                ShowInTaskbar = false
            })
            {
                PropertyGrid pg = new PropertyGrid()
                {
                    Dock = DockStyle.Fill,
                    PropertySort = PropertySort.NoSort,
                    SelectedObject = UserSettings.TheSettings
                };

                // Detect changes of interest.
                bool ctrls = false;
                pg.PropertyValueChanged += (sdr, args) =>
                {
                    string p = args.ChangedItem.PropertyDescriptor.Name;
                    ctrls |= (p.Contains("Font") | p.Contains("Color"));
                };

                f.Controls.Add(pg);
                f.ShowDialog();

                if (ctrls)
                {
                    MessageBox.Show("UI changes require a restart to take effect.");
                }

                // Always safe to update these.
                SetUiTimerPeriod();
                _surface.TopMost = UserSettings.TheSettings.LockUi;

                SaveSettings();
            }
        }
        #endregion

        #region Play control
        /// <summary>
        /// Update everything per param.
        /// </summary>
        /// <param name="cmd">The command.</param>
        /// <param name="userAction">Something the user did.</param>
        /// <returns>Indication of success.</returns>
        bool ProcessPlay(PlayCommand cmd, bool userAction)
        {
            bool ret = true;

            if(_script != null)
            {
                switch (cmd)
                {
                    case PlayCommand.Start:
                        bool ok = _needCompile ? Compile() : true;
                        if (ok)
                        {
                            _startTime = DateTime.Now;
                            btnPlay.Checked = true;
                            _surface.InitSurface(_script);
                        }
                        else
                        {
                            btnPlay.Checked = false;
                            ret = false;
                        }
                        break;

                    case PlayCommand.Stop:
                        btnPlay.Checked = false;
                        break;
                }

                btnPlay.BackColor = btnPlay.Checked ? UserSettings.TheSettings.SelectedColor : SystemColors.Control;
            }
            else
            {
                btnPlay.Checked = false;
                ret = false;
            }

            return ret;
        }
        #endregion

        #region Keyboard handling
        /// <summary>
        /// Do some global key handling. Space bar is used for stop/start playing.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            if(e.KeyCode == Keys.Space)
            {
                // Handle start/stop toggle.
                ProcessPlay(btnPlay.Checked ? PlayCommand.Stop : PlayCommand.Start, true);
                e.Handled = true;
            }
        }
        #endregion

        #region Internal stuff
        /// <summary>
        /// Common func.
        /// </summary>
        void SetUiTimerPeriod()
        {
            // Convert fps to msec per frame.
            double framesPerMsec = (double)_frameRate / 1000;
            double msecPerFrame = 1 / framesPerMsec;
            _timer.SetTimer("UI", (int)msecPerFrame);
        }

        /// <summary>
        /// The meaning of life.
        /// </summary>
        void About_Click(object sender, EventArgs e)
        {
            // Main help file.
            string mdText = File.ReadAllText(@"README.md");

            // Put it together.
            List<string> htmlText = new List<string>();

            // Boilerplate
            htmlText.Add($"<!DOCTYPE html><html><head><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            // CSS
            htmlText.Add($"<style>body {{ background-color: {UserSettings.TheSettings.BackColor.Name}; font-family: \"Arial\", Helvetica, sans-serif; }}");
            htmlText.Add($"</style></head><body>");

            // Meat.
            string mdHtml = string.Join(Environment.NewLine, mdText);
            htmlText.Add(mdHtml);

            // Bottom.
            string ss = "<!-- Markdeep: --><style class=\"fallback\">body{visibility:hidden;white-space:pre;font-family:monospace}</style><script src=\"markdeep.min.js\" charset=\"utf-8\"></script><script src=\"https://casual-effects.com/markdeep/latest/markdeep.min.js\" charset=\"utf-8\"></script><script>window.alreadyProcessedMarkdeep||(document.body.style.visibility=\"visible\")</script>";
            htmlText.Add(ss);
            htmlText.Add($"</body></html>");

            string fn = Path.Combine(Path.GetTempPath(), "nprocessing.html");
            File.WriteAllText(fn, string.Join(Environment.NewLine, htmlText));
            Process.Start(fn);
        }
        #endregion
    }
}
