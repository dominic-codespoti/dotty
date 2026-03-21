content = open("Views/MainWindow.axaml", "r").read()
content = content.replace("""<layout:SplitPaneHost DataContext="{Binding RootNode}" 
                                           IsVisible="{Binding IsActive}" />""", """<Border IsVisible="{Binding IsActive}">
                         <layout:SplitPaneHost DataContext="{Binding RootNode}" />
                     </Border>""")
with open("Views/MainWindow.axaml", "w") as f:
    f.write(content)
