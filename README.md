# Upload2NC
Upload to NextCloud and Create Sharing link

Setting used in **appsettings.json.**

```
{
    "Serilog": {
      "Using": [ "Serilog.Sinks.Console", "Serilog.Sinks.File" ],
      "MinimumLevel": "Debug",
      "WriteTo": [
        { "Name": "Console" },
        {
          "Name": "File",
          "Args": { "path": "_logs/log.txt" }
        }
      ],
      "Properties": {
        "Application": "Serilog-Demo"
      }
    },
    "NextCloud": {
      "Hostname": "website.com",
      "Port": 443,
      "Username": "username",
      "Password": "password",
      "RootPath": "/fileshare/remote.php/dav/files/username/",
      "UploadFolder": "_uploads",
      "OCSEndPoint": "/fileshare/ocs/v2.php/apps/files_sharing/api/v1/"
    },
    "Connection": {
      "DataSource": "ip:port/sid",
      "UserID": "userid",
      "Password": "password"
    }
}
```

Items to modify:
- Username
- Password
- username in RootPath
- DataSource
- UserID
- Password
