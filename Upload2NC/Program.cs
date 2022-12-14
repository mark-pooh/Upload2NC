using Microsoft.Extensions.Configuration;
using Oracle.ManagedDataAccess.Client;
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
            string rootDir = string.Format("{0}", Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
            string settingFile = Path.Combine(rootDir, "appsettings.json");

            if (File.Exists(settingFile))
            {
                IConfigurationRoot configuration = ConfigureLogger();

                try
                {
                    string filename, localFile, remoteFile = string.Empty;
                    Response linkResponse = new();

                    #region Configure nextcloud

                    string hostname = configuration.GetValue<string>("NextCloud:Hostname");
                    string port = configuration.GetValue<int>("NextCloud:Port").ToString();
                    string username = configuration.GetValue<string>("NextCloud:Username");
                    string password = configuration.GetValue<string>("NextCloud:Password");
                    string rootPath = configuration.GetValue<string>("NextCloud:RootPath");
                    string uploadDir = configuration.GetValue<string>("NextCloud:UploadFolder");
                    string ocsEndpoint = configuration.GetValue<string>("NextCloud:OCSEndPoint");

                    hostname = (port == "443") ? string.Format("https://{0}", hostname) : string.Format("http://{0}", hostname);

                    #endregion

                    if (Directory.Exists(uploadDir))
                    {
                        var files = Directory.GetFiles(uploadDir);

                        #region Setting DB connection

                        string dbDataSource = configuration.GetValue<string>("Connection:DataSource");
                        string dbUserID = configuration.GetValue<string>("Connection:UserID");
                        string dbPassword = configuration.GetValue<string>("Connection:Password");

                        string connString = string.Format("Data Source={0};User Id={1};Password={2}", dbDataSource, dbUserID, dbPassword);

                        #endregion

                        if (files.Length > 0)
                        {
                            foreach (string file in files)
                            {
                                filename = Path.GetFileName(file);
                                localFile = Path.Combine(rootDir, uploadDir, filename);
                                remoteFile = string.Format("{0}{1}/{2}", rootPath, uploadDir, filename);

                                bool proceed = false;

                                //Call synchronous API - Upload
                                proceed = Upload(hostname, username, password, remoteFile, localFile);

                                if (proceed)
                                {
                                    //Call synchronous API - Create shared link from uploaded file
                                    linkResponse = CreateLink(hostname, username, password, ocsEndpoint, string.Format("{0}/{1}", uploadDir, filename));

                                    if (linkResponse.Status == "success")
                                    {
                                        #region Update DB link value

                                        OracleConnection conn = new(connString);
                                        conn.Open();

                                        OracleCommand cmd = new()
                                        {
                                            Connection = conn,
                                            CommandText = "UPDATE NEPSCOMSOF.WV_JOB_BND SET DOWNLOAD_LINK = :link WHERE FILENAME = :filename",
                                            CommandType = System.Data.CommandType.Text
                                        };
                                        cmd.Parameters.Add(":link", linkResponse.Message);
                                        cmd.Parameters.Add(":filename", filename);

                                        var procResponse = cmd.ExecuteNonQuery();

                                        if(procResponse > 0) File.Delete(localFile);

                                        cmd.Dispose();                                       

                                        conn.Dispose();
                                        conn.Close();

                                        #endregion

                                        Log.Information("Shared link for {0} : {1}", filename, linkResponse.Message);
                                    }
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
                catch (Exception ex)
                {
                    Log.Error(ex.ToString());
                }

                Log.CloseAndFlush();
            }
            else
            {
                File.Create(settingFile);
            }
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

        static Response CreateLink(string hostname, string username, string password, string endpoint, string remoteFile)
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
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");

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

        static string GetLink(string hostname, string username, string password, string endpoint, string filename)
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

            //Should have retrieve id but its ok for now
            var elements = (from x in incomingXml.Descendants("element")
                            where x.Element("file_target").Value.Contains(filename)
                            select x).SingleOrDefault();

            return elements.Element("url").Value;
        }

        static IConfigurationRoot ConfigureLogger()
        {
            IConfigurationRoot configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json")
                .Build();

            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .CreateLogger();

            return configuration;
        }
    }

    class Response
    {
        public string? Status { get; set; }
        public string? Message { get; set; }
    }
}

/*
 * https://stackoverflow.com/questions/31453495/how-to-read-appsettings-values-from-a-json-file-in-asp-net-core
 */