using System;
using System.Collections.Generic;
using System.Deployment.Application;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ContinuousDeployment
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            Version.Text = ApplicationDeployment.IsNetworkDeployed ? ApplicationDeployment.CurrentDeployment.CurrentVersion.ToString() : Assembly.GetExecutingAssembly().GetName().Version.ToString();

            var _ = Loop();

            RegisterForAutostart();

            TryUpdatingApplication();
        }

        private string RepositoryRoot => $@"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}\source\repos";

        private void RegisterForAutostart()
        {
            string[] mystrings = new string[] { $@"@echo off

IF EXIST ""%appdata%\Microsoft\Windows\Start Menu\Programs\{typeof(App).Namespace}\{typeof(App).Namespace}.appref-ms"" (
""%appdata%\Microsoft\Windows\Start Menu\Programs\{typeof(App).Namespace}\{typeof(App).Namespace}.appref-ms""
) ELSE (start /b """" cmd /c del ""%~f0""&exit /b)" };

            string fullPath = $"%appdata%\\Microsoft\\Windows\\Start Menu\\Programs\\Startup\\Launch {typeof(App).Namespace}.cmd";

            //Expands the %appdata% path and writes the file to the Startup folder
            File.WriteAllLines(Environment.ExpandEnvironmentVariables(fullPath), mystrings);
        }

        private void TryUpdatingApplication()
        {
            if (ApplicationDeployment.IsNetworkDeployed)
            {
                var deployment = ApplicationDeployment.CurrentDeployment;
                try
                {
                    if (deployment.CheckForDetailedUpdate().UpdateAvailable)
                    {
                        try
                        {
                            deployment.Update();
                            System.Windows.Forms.Application.Restart();
                            Application.Current.Shutdown();
                        }
                        catch (DeploymentDownloadException)
                        {
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        private async Task Loop()
        {
            while (true)
            {
                var request = await WaitForRequestAsync();
                LogMessage($"Got deployment request for '{request.deploymentType}' to {request.repository}\n", Brushes.SlateBlue);
                var status = await PullUpdatesFromGithub(request.repository);
                LogMessage($"Github:\n{status}", Brushes.ForestGreen);
                var projects = await FindAllProjects(request.repository, request.deploymentType);
                LogMessage($"Projects to build: {projects.Length}\n", Brushes.White);
                await BuildProjects(projects);
                LogMessage($"done", Brushes.White);

                TryUpdatingApplication();
            }
        }

        private async Task BuildProjects((string name, string project, string publishTo)[] projects)
        {
            // this atm only works for dotnet core apps.
            // for properly building and deploying clickonce apps, need to gather the current version deployed on server, then run this:
            /// C:\Program Files (x86)\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin>msbuild c:\Users\davep\source\repos\continuous-deployment\src\ContinuousDeployment\ContinuousDeployment\ContinuousDeployment.csproj /target:publish /p:Configuration=Release;PublishDir=C:\Users\davep\Desktop\test\;ApplicationVersion=1.2.3.5
            await Task.WhenAll(projects.Select(async project =>
            {
                var index = LogMessage($"Building {project.name} to {project.publishTo}", Brushes.DeepSkyBlue);
                File.WriteAllText($@"{project.publishTo}\app_offline.htm", @"<html><head><style>html { background: black }</style><meta http-equiv='refresh' content='2'></head></html>");

                var output = await AsyncProcessHelper.RunAsync("dotnet", $"publish -o {project.publishTo} {project.project}");
                LogMessage(index, string.Join("\n", output.Split('\n').Skip(3)), Brushes.LightGoldenrodYellow);
                File.Delete($@"{project.publishTo}\app_offline.htm");
            }));
        }

        private Task<(string name, string project, string publishTo)[]> FindAllProjects(string repository, string deploymentType)
        {
            // this atm only works for dotnet core apps. have to find other projects, too (mainly, ones with clickonce in it)
            var solutionPath = $@"{RepositoryRoot}\{repository}";
            var projectFiles = Directory.GetFiles(solutionPath, "*.csproj", SearchOption.AllDirectories)
                .Select(project => new
                {
                    ProjectPath = project,
                    PublishPath = Directory.GetFiles(Path.GetDirectoryName(project), $"{deploymentType}.pubxml", SearchOption.AllDirectories).FirstOrDefault()
                })
                .Where(project => project.PublishPath != default);

            var projects = projectFiles.Select(project =>
            {
                var profile = File.ReadAllText(project.PublishPath);
                var _ = profile.Substring(profile.IndexOf("<publishUrl>") + "<publishUrl>".Length);
                var path = _.Substring(0, _.IndexOf("</publishUrl>"));

                return (name: Path.GetFileNameWithoutExtension(project.ProjectPath), project: project.ProjectPath, publishTo: path);
            });

            return Task.FromResult(projects.ToArray());
        }

        private TextBlock LogMessage(string message, Brush color)
        {
            var textBlock = new TextBlock
            {
                Text = message,
                Foreground = color
            };
            Log.Children.Add(textBlock);
            Console.ScrollToBottom();
            return textBlock;
        }

        private void LogMessage(TextBlock after, string message, Brush color)
        {
            var textBlock = new TextBlock
            {
                Text = message,
                Foreground = color
            };
            for (var index = 0; index < Log.Children.Count; index++)
            {
                if (Log.Children[index] == after)
                {
                    Log.Children.Insert(index + 1, textBlock);
                    return;
                }
            }
            Log.Children.Add(textBlock);
            Console.ScrollToBottom();
        }

        private async Task<string> PullUpdatesFromGithub(string repository)
        {
            await AsyncProcessHelper.RunAsync("git", "fetch origin master", $@"{RepositoryRoot}\{repository}");
            await AsyncProcessHelper.RunAsync("git", "reset --hard origin/master", $@"{RepositoryRoot}\{repository}");
            return await AsyncProcessHelper.RunAsync("git", "show --stat --oneline HEAD", $@"{RepositoryRoot}\{repository}");
        }

        /// <summary>
        /// Listens to http://localhost:5000/?deploymenttype=Server%20Deployment&repository=website
        /// </summary>
        private async Task<(string deploymentType, string repository)> WaitForRequestAsync()
        {
            using (var listener = new HttpListener())
            {
                listener.Prefixes.Add("http://localhost:5000/");
                listener.Start();

                var context = await listener.GetContextAsync();

                var request = (
                    deploymentType: context.Request.QueryString["deploymenttype"],
                    repository: context.Request.QueryString["repository"]
                );

                context.Response.StatusCode = 200;
                using (var writer = new StreamWriter(context.Response.OutputStream, System.Text.Encoding.UTF8))
                {
                    writer.WriteLine("ok");
                }
                context.Response.Close();

                return request;
            }
        }
    }
}
