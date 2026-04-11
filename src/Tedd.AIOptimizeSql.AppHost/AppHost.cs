var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Tedd_AIOptimizeSql_WebUI>("tedd-aioptimizesql-webui");

builder.AddProject<Projects.Tedd_AIOptimizeSql_Worker>("tedd-aioptimizesql-worker");

builder.Build().Run();
