﻿using System;
using Foundatio.Logging;
using Foundatio.ServiceProviders;

namespace Foundatio.Tests.Jobs {
    public class MyServiceProvider : IServiceProvider {
       public object GetService(Type type) {
            if (type == typeof(WithDependencyJob))
                return new WithDependencyJob(new MyDependency { MyProperty = 5 });
            
            if (type == typeof(MyWorkItemHandler))
                return new MyWorkItemHandler(new MyDependency { MyProperty = 5 });

            return Activator.CreateInstance(type);
        }
    }

    public class MyBootstrappedServiceProvider : BootstrappedServiceProviderBase {
        protected override IServiceProvider BootstrapInternal(ILoggerFactory loggerFactory) {
            // create container, do registrations and return the service provider instance.
            return new MyServiceProvider();
        }
    }
}
