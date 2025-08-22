cls
dotnet build ..\Relatude.DB.sln --configuration Release

.\nuget pack Relatude.DB.Server.nuspec -OutputDirectory Output\
.\nuget pack Relatude.DB.Plugins.Azure.nuspec -OutputDirectory Output\
.\nuget pack Relatude.DB.Plugins.Lucene.nuspec -OutputDirectory Output\
.\nuget pack Relatude.DB.Plugins.Sqlite.nuspec -OutputDirectory Output\
