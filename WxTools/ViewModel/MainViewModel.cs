﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using log4net;
using WxTools.Annotations;
using WxTools.Client.Dal;
using WxTools.Client.Helper;
using WxTools.Client.Model;
using WxTools.Theme;

namespace WxTools.Client.ViewModel
{
    public class MainViewModel : INotifyPropertyChanged
    {
        #region 字段

        public static MainViewModel Instance { get; } = new MainViewModel();

        public ObservableCollection<OperaDal> Operas
        {
            get => _operas;
            set
            {
                if (Equals(value, _operas)) return;
                _operas = value;
                OnPropertyChanged();
            }
        }

        private readonly ILog _log = LogManager.GetLogger(typeof(MainViewModel));

        private ObservableCollection<OperaDal> _operas;

        private readonly Queue<string> _urlQueue = new Queue<string>();

        private Thread _thread;

        private DateTime _lasTime;

        private bool _runstate;

        private bool _isExit;

        public TcpClientDal TcpClientDal { get; } = new TcpClientDal();

        #endregion

        #region 构造函数

        public MainViewModel()
        {
            Operas = new ObservableCollection<OperaDal>();
            RegisterMessenger();
            CheckUpdate();
            TcpClientDal.ConnectedAction = () =>
            {
                UrlQueueThread();
                CheckHwndStateThread();
            };
            TcpClientDal.Connect();
        }

        #endregion

        #region 命令

        /// <summary>
        /// 窗体关闭事件
        /// </summary>
        public RelayCommand ClosedCommand { get; } = new RelayCommand(() =>
        {
            Instance._isExit = true;
            //Instance.IsChecked = false;
           /* foreach (var opera in Instance.Operas)
            {
                opera.StopThread();
                opera.Dispose();
            }*/
            Instance.TcpClientDal.Dispose();
            Instance.Operas.Clear();
            LwFactory.Clear();
        });

        public RelayCommand OpenWeixinCommand { get; } = new RelayCommand(() =>
        {
            var path = @"C:\Program Files (x86)\Tencent\WeChat\WeChat.exe";
            if (File.Exists(path))
            {
                try
                {
                    var strCmd = new StringBuilder("cd \"C:\\Program Files (x86)\\Tencent\\WeChat\"&start ");
                    for (int i = 0; i < 5; i++)
                    {
                        strCmd.Append("WeChat.exe&");
                    }
                    Process myProcess = new Process();
                    ProcessStartInfo myProcessStartInfo = new ProcessStartInfo("cmd.exe")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true
                    };
                    myProcess.StartInfo = myProcessStartInfo;
                    myProcessStartInfo.Arguments = "/c " + strCmd.ToString().TrimEnd('&');
                    myProcess.Start();
                    myProcess.Close();
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            }
        });

        public RelayCommand RunCommand { get; } = new RelayCommand(() =>
        {
            //Instance.Run();
        });

        #endregion

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler PropertyChanged;

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion

        #region 方法

        //注册消息监听
        private void RegisterMessenger()
        {
            Common.Messenger.Register("CefWebViewWnd", () =>
            {
                _lasTime = DateTime.Now;
                //Interlocked.Increment(ref Common.SessionCount);
                Common.SessionCount += 1;

                if (Common.SessionCount >= Common.MaxSessionCount || Common.SessionCount == Operas.Count)
                {
                    _log.Info($"清理一次窗口; SessionCount:{Common.SessionCount};" +
                              $" Operas.Count:{Operas.Count}; MaxSessionCount:{Common.MaxSessionCount};");
                    //超过窗口数 或者 正好
                    Thread.Sleep(4000);
                    Common.SessionCount = 0;
                    CloseCefWebViewWnd();
                }
            });
        }

        //关闭CefWebViewWnd窗口
        private void CloseCefWebViewWnd()
        {
            var hwndstr = LwFactory.GetDefault().EnumWindow("微信", "CefWebViewWnd", null);
            if (hwndstr != null)
            {
                var hwnds = hwndstr.Split(',');
                foreach (var hwnd in hwnds)
                {
                    WinApi.SendMessage(new IntPtr(int.Parse(hwnd)), 0x0010, 0, 0);
                }
                Console.WriteLine("All完成，全部关闭");
            }
        }

        //监测窗体，变量状态 线程
        private void CheckHwndStateThread()
        {
            if (_thread != null) return;
            _thread = new Thread(() =>
            {
                _log.Info("监测线程启动成功");
                Thread.Sleep(2000);
                var list = new List<OperaDal>();
                while (true)
                {
                    if (_isExit) return;
                    Thread.Sleep(2000);
                    if (TcpClientDal.Connected)
                    {
                        bool wxAddOrRemove = false;
                        foreach (var opera in Operas)
                        {
                            if (opera.Lw.GetWindowState(opera.Hwnd, 0) == 0)
                            {
                                //窗体不存在
                                list.Add(opera);
                            }
                            else
                            {
                                if (opera.Lw.GetWindowState(opera.Hwnd, 3) == 1)
                                {
                                    //窗体最小化了
                                    _log.Warn($"微信被窗体最小化了，已经恢复，hwnd={opera.Hwnd}");
                                    opera.Lw.SetWindowState(opera.Hwnd, 7);
                                }
                                if (opera.Lw.GetClientSize(opera.Hwnd) == 1)
                                {
                                    if (opera.Lw.X() != Client.Common.Width || opera.Lw.Y() != Client.Common.Height)
                                    {
                                        opera.Lw.SetWindowSize(opera.Hwnd, Client.Common.Width, Client.Common.Height);
                                        _log.Warn("微信窗体大小被用户拖动，已经恢复");
                                    }
                                }
                            }
                        }
                        foreach (var dal in list)
                        {
                            _log.Warn($"微信窗口出错了，hwnd={dal.Hwnd}");
                            dal.Dispose();
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                lock (Operas)
                                    Operas.Remove(dal);
                                wxAddOrRemove = true;
                            });
                        }

                        //防止文章窗口打开过久
                        if (Client.Common.SessionCount > 0 && (DateTime.Now - _lasTime).Seconds > 30)
                        {
                            _log.Warn("文章窗口打开过久，已经关闭");
                            Client.Common.SessionCount = 0;
                            CloseCefWebViewWnd();
                        }

                        #region 新的微信

                        var hwndstr = LwFactory.GetDefault().EnumWindow(null, "WeChatMainWndForPC", null);
                        var hwnds = hwndstr?.Split(',');
                        if (hwnds?.Length > Operas.Count)
                        {
                            foreach (var hwnd in hwnds)
                            {
                                if (Operas.All(o => o.Hwnd != int.Parse(hwnd)))
                                {
                                    wxAddOrRemove = true;
                                    _log.Info("新的微信");
                                    var newlw = Operas.Count == 0 ? LwFactory.GetDefault() : LwFactory.GetNew();
                                    Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        var opera = new OperaDal(newlw);
                                        try
                                        {
                                            opera.Hwnd = int.Parse(hwnd);
                                            opera.Load();
                                            opera.Lw.SetWindowState(opera.Hwnd, 1);
                                            lock (Operas)
                                                Operas.Add(opera);
                                        }
                                        catch (Exception e)
                                        {
                                            _log.Error($"{opera.Hwnd}加载出错", e);
                                            MessageBox.Show($"{opera.Hwnd}加载出错", "提示");
                                        }
                                    });
                                }
                            }
                        }

                        #endregion

                        if (_isExit) return;
                        if (TcpClientDal.CheckIsConnected())
                        {
                            //心跳包
                            TcpClientDal.SendHeartbeat();
                        }
                        else
                        {
                            //服务器被断开
                            TcpClientDal.Connect();
                            Thread.Sleep(5000);
                        }

                        //通知服务器微信数量
                        if (wxAddOrRemove)
                        {
                            TcpClientDal.SendWxCount(Operas.Count);
                        }
                    }
                }
            })
            {
                IsBackground = true,
                Name = "检测状态线程"
            };
            _thread.Start();
        }

        //检测程序更新
        private void CheckUpdate()
        {
            new Thread(() =>
            {
                while (true)
                {
                    if (_isExit) return;
                    if (Operas != null && Operas.All(o => o.RunState == RunState.Idle))
                    {
                        Common.StartUpdate();
                    }
                    Thread.Sleep(60000);
                }
            }) {IsBackground = true}.Start();
        }

        public void ExecuteUrl(string url)
        {
            _urlQueue.Enqueue(url);
        }

        private readonly object _lock = new object();
        private void UrlQueueThread()
        {
            if (_runstate) return;
            _runstate = true;
            var thread = new Thread(() =>
                {
                    while (true)
                    {
                        if (_urlQueue.Count > 0)
                        {
                            var url = _urlQueue.Dequeue();
                            int index = -1;
                            Task[] tasks = new Task[Common.MaxThreadCount];
                            for (int i = 0; i < tasks.Length; i++)
                            {
                                tasks[i] = Task.Factory.StartNew(() =>
                                {
                                    while (true)
                                    {
                                        OperaDal opear;
                                        int ic;
                                        lock (_lock)
                                        {
                                            index++;
                                            if (index >= Operas.Count) return;
                                            _log.Info($"ExecuteUrl Operas[{index}]开始执行");
                                            opear = Operas[index];
                                            ic = index;
                                        }
                                        opear?.SendMyMessage(url, ic);
                                    }
                                });
                            }
                            Task.WaitAll(tasks);
                            _log.Info("执行url完毕");
                            TcpClientDal.SendLog("--全部完成--");
                        }
                        Thread.Sleep(50);
                    }
                })
                {IsBackground = true, Name = "Url执行线程"};
            thread.Start();
        }

        #endregion
    }
}
