﻿using System.Collections.Generic;
using JoySoftware.HomeAssistant.Model;
using NetDaemon.Common;
namespace NetDaemon.Service.App.CodeGeneration
{
    public interface ICodeGenerator
    {
        string? GenerateCodeRx(string nameSpace, IReadOnlyCollection<EntityState> entities, IReadOnlyCollection<HassServiceDomain> services);
    }
}
