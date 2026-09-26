CREATE TABLE IF NOT EXISTS browser_records (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(100) NOT NULL, email VARCHAR(150), status VARCHAR(30) DEFAULT 'active');
CREATE TABLE IF NOT EXISTS lookup_people (id BIGINT PRIMARY KEY, name VARCHAR(100) NOT NULL, email VARCHAR(100));
INSERT IGNORE INTO lookup_people (id,name,email) VALUES (9007199254740993,'Alice Friendly','alice.lookup@example.test'),(42,'Bob Friendly','bob.lookup@example.test');
CREATE TABLE IF NOT EXISTS lookup_orders (id INT AUTO_INCREMENT PRIMARY KEY, title VARCHAR(100) NOT NULL, person_id BIGINT NULL, FOREIGN KEY (person_id) REFERENCES lookup_people(id));
CREATE TABLE IF NOT EXISTS z_editor_records (id INT AUTO_INCREMENT PRIMARY KEY, title VARCHAR(100) NOT NULL, status VARCHAR(30) NOT NULL DEFAULT 'draft', day DATE NULL, happened DATETIME(6) NULL, stamped TIMESTAMP(6) NULL);
CREATE TABLE IF NOT EXISTS z_required_records (id INT AUTO_INCREMENT PRIMARY KEY, title VARCHAR(100) NULL DEFAULT 'default title', note TEXT);
CREATE TABLE IF NOT EXISTS z_list_view_records (id INT PRIMARY KEY, title VARCHAR(100), status VARCHAR(20), amount DECIMAL(10,2));
INSERT IGNORE INTO z_list_view_records (id,title,status,amount) VALUES (1,'Alpha','open',10),(2,'Beta','open',20),(3,'Gamma','closed',99),(4,'Delta','open',5);

CREATE TABLE IF NOT EXISTS z_copy_catalog (id BIGINT PRIMARY KEY, name VARCHAR(100), price DECIMAL(20,4));
INSERT IGNORE INTO z_copy_catalog (id,name,price) VALUES (9007199254740993,'Copy Alice',12.345),(42,'Copy Bob',25),(43,'Excluded product',0);
CREATE TABLE IF NOT EXISTS z_copy_records (id INT AUTO_INCREMENT PRIMARY KEY, product_id BIGINT NULL, description VARCHAR(100), unit_price DECIMAL(20,4), quantity INT);

CREATE TABLE IF NOT EXISTS z_formula_dropdown_records (id INT AUTO_INCREMENT PRIMARY KEY, title VARCHAR(100), status VARCHAR(30) NULL);
CREATE TABLE IF NOT EXISTS z_page_customers (id BIGINT PRIMARY KEY,name VARCHAR(100),email VARCHAR(100),order_total DECIMAL(18,4),order_count INT);
INSERT IGNORE INTO z_page_customers (id,name,email) VALUES (9007199254740993,'Page Alice','alice.page@example.test'),(42,'Page Bob','bob.page@example.test');
CREATE TABLE IF NOT EXISTS z_page_orders (id INT AUTO_INCREMENT PRIMARY KEY,customer_id BIGINT,title VARCHAR(100),amount DECIMAL(12,2),copied_email VARCHAR(100));
INSERT IGNORE INTO z_page_orders (id,customer_id,title,amount) VALUES (1,9007199254740993,'Alice order',25),(2,42,'Bob private order',50);
CREATE TABLE IF NOT EXISTS z_page_lines (order_id INT,seq INT,title VARCHAR(100),PRIMARY KEY(order_id,seq));
INSERT IGNORE INTO z_page_lines (order_id,seq,title) VALUES (1,1,'Alice first line'),(1,2,'Alice second line'),(2,1,'Bob private line');

CREATE TABLE IF NOT EXISTS z_field_records (id BIGINT AUTO_INCREMENT PRIMARY KEY,name VARCHAR(100),note VARCHAR(100),secret VARCHAR(100));
INSERT IGNORE INTO z_field_records (id,name,note,secret) VALUES(9007199254740993,'Field visible','Read only note','BROWSER_SECRET');
