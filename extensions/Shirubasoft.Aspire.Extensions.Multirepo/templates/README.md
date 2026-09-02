# Shirubasoft.Aspire.Extensions.Multirepo.Templates

Install the template package and add a typed module contract to a project that references
`Shirubasoft.Aspire.Extensions.Multirepo`:

```bash
dotnet new install Shirubasoft.Aspire.Extensions.Multirepo.Templates
dotnet new aspire-module --name OrdersModule --moduleName orders --namespace Orders.Modules
```

The generated contract starts with an nginx container so it can be materialized immediately. Replace that resource with the projects, containers, and integrations owned by the module.
