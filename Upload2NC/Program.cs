﻿using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Serilog;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Xml.Linq;

namespace Upload2NC
{
    class Program
    {
        static void Main()
        {
            ConfigureLogger();

            try
            {
                string rootDir = string.Format("{0}", Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
                string settingFile = Path.Combine(rootDir, "settings.txt");
                string filename, localFile, remoteFile = string.Empty;
                Response linkResponse = new();

                if (File.Exists(settingFile))
                {
                    string[] data = File.ReadAllLines(settingFile);

                    string hostname = data[0].Split(':')[1].Trim();
                    string port = data[1].Split(':')[1].Trim();
                    string username = data[2].Split(':')[1].Trim();
                    string password = data[3].Split(':')[1].Trim();
                    string rootPath = data[4].Split(':')[1].Trim();
                    string uploadDir = data[5].Split(':')[1].Trim();
                    string ocsEndpoint = data[6].Split(':')[1].Trim();

                    hostname = (port == "443") ? string.Format("https://{0}", hostname) : string.Format("http://{0}", hostname);

                    if (Directory.Exists(uploadDir))
                    {
                        var files = Directory.GetFiles(uploadDir);

                        if (files.Length > 0)
                        {
                            foreach(string file in files)
                            {
                                filename = Path.GetFileName(file);
                                localFile = Path.Combine(rootDir, uploadDir, filename);
                                remoteFile = string.Format("{0}{1}/{2}", rootPath, uploadDir, filename);

                                bool proceed = false;

                                //Call synchronous API
                                proceed = Upload(hostname, username, password, remoteFile, localFile);

                                if (proceed)
                                {
                                    linkResponse = CreateLink(hostname, username, password, ocsEndpoint, string.Format("{0}/{1}", uploadDir, filename), localFile, filename);

                                    if (linkResponse.Status == "success") Log.Information("Shared link for {0} : {1}", filename, linkResponse.Message);
                                    else Log.Error("Unable to create share link. \nMessage: {0}", linkResponse.Message);
                                }
                            }
                        }
                    }
                    else
                    {
                        Directory.CreateDirectory(uploadDir);
                    }
                }
                else
                {
                    using StreamWriter sw = File.AppendText(settingFile);
                    sw.Write("hostname: spt.tm.com.my\nport: 443\nusername: \npassword: \nrootPath: \nuploadFolder: \nOCSEndPoint: /fileshare/ocs/v2.php/apps/files_sharing/api/v1/");
                }
            }
            catch(Exception ex)
            {
                Log.Error(ex.ToString());
            }

            Log.CloseAndFlush();
        }

        static bool Upload(string hostname, string username, string password, string remoteFile, string localFile)
        {
            string url = string.Format("{0}{1}", hostname, remoteFile);
            string auth = string.Format("{0}:{1}", username, password);

            using var httpClient = new HttpClient();
            using var request = new HttpRequestMessage(new HttpMethod("PUT"), url);
            request.Headers.TryAddWithoutValidation("OCS-APIRequest", "true");

            var base64authorization = Convert.ToBase64String(Encoding.ASCII.GetBytes(auth));
            request.Headers.TryAddWithoutValidation("Authorization", $"Basic {base64authorization}");

            request.Content = new ByteArrayContent(File.ReadAllBytes(localFile));

            var response = httpClient.Send(request);
            using var reader = new StreamReader(response.Content.ReadAsStream());

            if(reader.ReadToEnd() == "") return true;
            return false;
        }

        static Response CreateLink(string hostname, string username, string password, string endpoint, string remoteFile, string localFile, string filename)
        {
            /*
             * Share Types
             * 0 = user
             * 1 = group
             * 3 = public link
             * 4 = email
             * 6 = federated cloud share
             * 7 = circle
             * 10 = talk convo
             */
            string shareType = "3";
            string url = string.Format("{0}{1}shares?shareType={2}&path={3}", hostname, endpoint, shareType, remoteFile);
            string data = "{ \"shareType\": \"" + shareType + "\", \"path\": \"" + remoteFile + "\" }";
            string auth = string.Format("{0}:{1}", username, password);

            using var httpClient = new HttpClient();
            using var request = new HttpRequestMessage(new HttpMethod("POST"), url);
            request.Headers.TryAddWithoutValidation("OCS-APIRequest", "true");

            var base64authorization = Convert.ToBase64String(Encoding.ASCII.GetBytes(auth));
            request.Headers.TryAddWithoutValidation("Authorization", $"Basic { base64authorization }");

            request.Content = new StringContent(data);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-www-form-urlencoded");

            var response = httpClient.Send(request);
            using var reader = new StreamReader(response.Content.ReadAsStream());

            var responseContent = reader.ReadToEnd();
            Response res = new();

            if (!responseContent.StartsWith("<") && !responseContent.EndsWith(">"))
            {
                res.Status = "fail";
                res.Message = "Response is not in XML\n Response: " + responseContent.ToString();
            }
            else
            {
                XElement incomingXml = XElement.Parse(responseContent);

                bool statusFail = (from x in incomingXml.Descendants("status")
                                   where x.Value == "failure"
                                   select x).Any();

                if (statusFail)
                {
                    var elements = (from x in incomingXml.Descendants("message")
                                    select x.Value).SingleOrDefault();

                    res.Status = "fail";
                    res.Message = elements.ToString();
                }
                else
                {
                    var elements = (from x in incomingXml.Descendants("data")
                                    select x).SingleOrDefault();

                    res.Status = "success";
                    res.Message = elements.Element("url").Value;
                }
            }

            return res;
        }

        static string GetLink(string hostname, string username, string password, string endpoint)
        {
            string url = string.Format("{0}{1}{2}", hostname, endpoint, "shares");
            string auth = string.Format("{0}:{1}", username, password);

            using var httpClient = new HttpClient();
            using var request = new HttpRequestMessage(new HttpMethod("GET"), url);
            request.Headers.TryAddWithoutValidation("OCS-APIRequest", "true");

            var base64authorization = Convert.ToBase64String(Encoding.ASCII.GetBytes(auth));
            request.Headers.TryAddWithoutValidation("Authorization", $"Basic {base64authorization}");

            var response = httpClient.Send(request);
            using var reader = new StreamReader(response.Content.ReadAsStream());

            XElement incomingXml = XElement.Parse(reader.ReadToEnd());

            var elements = (from x in incomingXml.Descendants("element")
                            where x.Element("file_target").Value.Contains("nomnom")
                            select x).SingleOrDefault();

            return elements.Element("url").Value;
        }

        static void ConfigureLogger()
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json")
                .Build();

            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .CreateLogger();
        }
    }

    class Response
    {
        public string? Status { get; set; }
        public string? Message { get; set; }
    }
}