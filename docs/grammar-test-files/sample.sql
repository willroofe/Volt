-- SQL grammar sample
CREATE TABLE editor_files (
  id INTEGER PRIMARY KEY,
  name VARCHAR(255) NOT NULL,
  modified_at TIMESTAMP
);

SELECT name, COUNT(*) AS total
FROM editor_files
WHERE id >= 1
GROUP BY name;
