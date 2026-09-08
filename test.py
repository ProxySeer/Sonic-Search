import re

pattern = 'battle*.exe'
escaped = re.escape(pattern)
print(escaped)

# In C#, Regex.Escape('battle*.exe') returns 'battle\*\.exe'
