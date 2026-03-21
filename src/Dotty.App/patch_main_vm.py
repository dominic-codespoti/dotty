import sys

content = open("ViewModels/ViewModels.cs", "r").read()

if "public bool IsActive" not in content:
    content = content.replace("public SplitNodeViewModel RootNode", "private bool _isActive;\n    public bool IsActive\n    {\n        get => _isActive;\n        set { _isActive = value; RaisePropertyChanged(); }\n    }\n\n    public SplitNodeViewModel RootNode")
    
    # In MainViewModel update IsActive based on ActiveTab
    setter = "get => _activeTab;\n        set\n        {\n            if (_activeTab != null) _activeTab.IsActive = false;\n            _activeTab = value;\n            if (_activeTab != null) _activeTab.IsActive = true;\n            RaisePropertyChanged();\n        }"
    
    content = content.replace("get => _activeTab;\n        set { _activeTab = value; RaisePropertyChanged(); }", setter)

with open("ViewModels/ViewModels.cs", "w") as f:
    f.write(content)

